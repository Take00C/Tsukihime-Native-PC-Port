"""Repository files the release depends on: the CI workflows, the line endings of the texts a package ships, the publish commands
of PUBLISHING.md, and the tables of SETUP_GUIDE.md, which SETUP_GUIDE.txt repeats in plain text.

PyYAML is not a pinned dependency, so the workflows are read by a small reader for the YAML they use: block mappings and
sequences, plain and quoted scalars, one-line flow sequences, literal block scalars and comments. It refuses anything else,
so a new construct needs the reader first. Every run: block must pass bash -n with its ${{ }} expressions replaced: in
round 5 the Mac download workflow never uploaded because of stray quotes in one.
"""
from __future__ import annotations

import os
import re
import shutil
import subprocess
from pathlib import Path

import pytest

REPO = Path(__file__).resolve().parents[2]
WORKFLOW_DIR = REPO / ".github" / "workflows"
WORKFLOWS = sorted(WORKFLOW_DIR.glob("*.yml"))
EXPRESSION = re.compile(r"\$\{\{.*?\}\}", re.S)
PINNED = re.compile(r"[\w.-]+/[\w.-]+(?:/[\w./-]+)?@[0-9a-f]{40}")
KEY = re.compile(r"([A-Za-z0-9_][A-Za-z0-9_.-]*):(?= |$)")
TOP_KEYS = {"name", "run-name", "on", "permissions", "env", "defaults", "concurrency", "jobs"}
JOB_KEYS = {"name", "runs-on", "needs", "if", "permissions", "environment", "concurrency", "outputs", "env", "defaults",
            "steps", "timeout-minutes", "strategy", "continue-on-error", "container", "services"}
STEP_KEYS = {"name", "id", "if", "uses", "run", "shell", "with", "env", "continue-on-error", "timeout-minutes",
             "working-directory"}
OTHER_SHELLS = {"pwsh", "powershell", "cmd", "python"}


class WorkflowSyntaxError(ValueError):
    pass


class _Reader:
    def __init__(self, text: str):
        self.lines = text.replace("\r\n", "\n").split("\n")
        for number, line in enumerate(self.lines, 1):
            if "\t" in line[:len(line) - len(line.lstrip(" \t"))]:
                raise WorkflowSyntaxError(f"line {number}: a tab in the indentation")

    @staticmethod
    def indent(line: str) -> int:
        return len(line) - len(line.lstrip(" "))

    def next(self, i: int) -> int:
        """The index of the next line that holds YAML, past blank and comment-only lines."""
        while i < len(self.lines) and (not self.lines[i].strip() or self.lines[i].lstrip().startswith("#")):
            i += 1
        return i

    def document(self):
        i = self.next(0)
        if i == len(self.lines):
            raise WorkflowSyntaxError("the file is empty")
        value, i = self.node(i, self.indent(self.lines[i]))
        i = self.next(i)
        if i < len(self.lines):
            raise WorkflowSyntaxError(f"line {i + 1}: unexpected indentation")
        return value

    def node(self, i: int, indent: int):
        content = self.lines[i][indent:]
        return self.sequence(i, indent) if content == "-" or content.startswith("- ") else self.mapping(i, indent)

    def child(self, i: int, indent: int):
        """The value of a key or dash with nothing after it: the more indented block below, else null."""
        j = self.next(i + 1)
        if j < len(self.lines) and self.indent(self.lines[j]) > indent:
            return self.node(j, self.indent(self.lines[j]))
        return None, i + 1

    def mapping(self, i: int, indent: int):
        result: dict = {}
        while True:
            i = self.next(i)
            if i == len(self.lines) or self.indent(self.lines[i]) < indent:
                return result, i
            if self.indent(self.lines[i]) > indent:
                raise WorkflowSyntaxError(f"line {i + 1}: unexpected indentation (a plain value cannot continue on the next line)")
            content = self.lines[i][indent:]
            if content == "-" or content.startswith("- "):
                if result and result[list(result)[-1]] is None:
                    raise WorkflowSyntaxError(f"line {i + 1}: a sequence at its key's indentation is not read; indent it")
                raise WorkflowSyntaxError(f"line {i + 1}: a sequence item where a key was expected")
            match = KEY.match(content)
            if not match:
                raise WorkflowSyntaxError(f"line {i + 1}: expected 'key: value', got {content!r}")
            key = match.group(1)
            if key in result:
                raise WorkflowSyntaxError(f"line {i + 1}: duplicate key {key!r}")
            rest = content[match.end():].strip()
            if not rest or rest.startswith("#"):
                result[key], i = self.child(i, indent)
            else:
                result[key], i = self.value(i, rest, indent)

    def sequence(self, i: int, indent: int):
        items: list = []
        while True:
            i = self.next(i)
            if i == len(self.lines) or self.indent(self.lines[i]) < indent:
                return items, i
            line = self.lines[i]
            if self.indent(line) > indent:
                raise WorkflowSyntaxError(f"line {i + 1}: unexpected indentation")
            content = line[indent:]
            if not (content == "-" or content.startswith("- ")):
                raise WorkflowSyntaxError(f"line {i + 1}: a key where a sequence item was expected")
            rest = content[1:].lstrip(" ")
            if not rest or rest.startswith("#"):
                item, i = self.child(i, indent)
            elif KEY.match(rest):
                column = len(line) - len(rest)
                self.lines[i] = " " * column + rest
                item, i = self.mapping(i, column)
            else:
                item, i = self.value(i, rest, indent)
            items.append(item)

    def value(self, i: int, rest: str, indent: int):
        number = i + 1
        if rest[0] in "|>":
            match = re.fullmatch(r"\|(-?)(?:\s+#.*)?", rest)
            if not match:
                raise WorkflowSyntaxError(f"line {number}: only | and |- block values are read, not {rest!r}")
            return self.block(i + 1, indent, keep_last_newline=not match.group(1))
        if rest[0] == "[":
            match = re.fullmatch(r"\[(.*)\](?:\s+#.*)?", rest)
            if not match:
                raise WorkflowSyntaxError(f"line {number}: a flow sequence must close on its own line")
            inner = match.group(1).strip()
            return ([self.scalar(part.strip(), number, flow=True) for part in inner.split(",")] if inner else []), i + 1
        return self.scalar(rest, number), i + 1

    def block(self, i: int, indent: int, keep_last_newline: bool):
        body: list[str] = []
        column = None
        while i < len(self.lines):
            line = self.lines[i]
            if line.strip():
                current = self.indent(line)
                if current <= indent:
                    break
                if column is None:
                    column = current
                elif current < column:
                    raise WorkflowSyntaxError(f"line {i + 1}: less indented than the block value above it")
                body.append(line[column:])
            else:
                body.append("")
            i += 1
        while body and not body[-1]:
            body.pop()
        text = "\n".join(body)
        return (text + "\n" if keep_last_newline and text else text), i

    @staticmethod
    def scalar(text: str, number: int, flow: bool = False) -> str:
        if not text:
            raise WorkflowSyntaxError(f"line {number}: an empty flow sequence item")
        if text[0] in "'\"":
            # 'single': '' is a quote; "double": only the escapes \\ \" \n \t \/
            quote = text[0]
            special = re.compile("'" if quote == "'" else r'["\\]')
            escapes = {"\\": "\\", '"': '"', "n": "\n", "t": "\t", "/": "/"}
            out, j = [], 1
            while True:
                match = special.search(text, j)
                if match is None:
                    raise WorkflowSyntaxError(f"line {number}: unterminated {quote}-quoted value")
                k = match.start()
                out.append(text[j:k])
                if quote == "'" and text[k + 1:k + 2] == "'":
                    out.append("'")
                    j = k + 2
                elif text[k] == "\\":
                    if text[k + 1:k + 2] not in escapes:
                        raise WorkflowSyntaxError(f"line {number}: unsupported escape in a double-quoted value")
                    out.append(escapes[text[k + 1]])
                    j = k + 2
                else:
                    after = text[k + 1:]
                    if after.strip() and not re.match(r"\s+#", after):
                        raise WorkflowSyntaxError(f"line {number}: text after a quoted value")
                    return "".join(out)
        if text[0] in "{&*!%@`>,?":
            raise WorkflowSyntaxError(f"line {number}: {text[0]!r} at the start of a value is not read (quote the value)")
        value = re.split(r"\s+#", text, maxsplit=1)[0].rstrip()
        if ": " in value or value.endswith(":"):
            raise WorkflowSyntaxError(f"line {number}: ': ' inside a plain value; quote it or use a | block")
        if flow and any(c in value for c in "[]{}"):
            raise WorkflowSyntaxError(f"line {number}: nested flow collections are not read")
        return value


def read_workflow(text: str):
    """The workflow as dicts, lists and strings; an empty value is None."""
    return _Reader(text).document()


def load(name: str) -> dict:
    return read_workflow((WORKFLOW_DIR / name).read_text(encoding="utf-8"))


def scalars(value):
    if isinstance(value, dict):
        for item in value.values():
            yield from scalars(item)
    elif isinstance(value, list):
        for item in value:
            yield from scalars(item)
    elif isinstance(value, str):
        yield value


def structure_problems(workflow) -> list[str]:
    if not isinstance(workflow, dict):
        return ["the workflow is not a mapping"]
    problems = [f"unknown top-level key {key!r}" for key in sorted(set(workflow) - TOP_KEYS)]
    problems += [f"an unclosed ${{{{ expression: {text!r}" for text in scalars(workflow) if "${{" in EXPRESSION.sub("", text)]
    jobs = workflow.get("jobs")
    if not isinstance(jobs, dict) or not jobs:
        return problems + ["no jobs"]
    for name, job in jobs.items():
        if not isinstance(job, dict):
            problems.append(f"job {name}: not a mapping")
            continue
        problems += [f"job {name}: unknown key {key!r}" for key in sorted(set(job) - JOB_KEYS)]
        if "runs-on" not in job:
            problems.append(f"job {name}: no runs-on")
        if not isinstance(job.get("steps"), list) or not job["steps"]:
            problems.append(f"job {name}: no steps")
            continue
        for index, step in enumerate(job["steps"], 1):
            where = f"job {name} step {index}"
            if not isinstance(step, dict):
                problems.append(f"{where}: not a mapping")
                continue
            problems += [f"{where}: unknown key {key!r}" for key in sorted(set(step) - STEP_KEYS)]
            if ("uses" in step) == ("run" in step):
                problems.append(f"{where}: needs exactly one of uses and run")
            if "uses" in step and not PINNED.fullmatch(str(step["uses"])):
                problems.append(f"{where}: {step['uses']} is not pinned to a full commit SHA")
            if "run" in step and not isinstance(step["run"], str):
                problems.append(f"{where}: run is not text")
            with_ = step.get("with") or {}
            if str(step.get("uses", "")).startswith("actions/upload-artifact@") and with_.get("archive") == "false" and "name" in with_:
                problems.append(f"{where}: upload-artifact ignores name with archive: false (the file name is the artifact name)")
    return problems


def find_bash() -> str | None:
    if os.name != "nt":
        return shutil.which("bash")
    # Git for Windows' bash; a bare "bash" on Windows can start WSL instead
    git = shutil.which("git")
    if git:
        for root in Path(git).resolve().parents[1:3]:
            for candidate in (root / "bin" / "bash.exe", root / "usr" / "bin" / "bash.exe"):
                if candidate.is_file():
                    return str(candidate)
    return None


def shell_problems(workflow: dict, bash: str) -> list[str]:
    problems = []
    for name, job in workflow["jobs"].items():
        default = (((job.get("defaults") or {}).get("run") or {}).get("shell")
                   or ((workflow.get("defaults") or {}).get("run") or {}).get("shell"))
        for index, step in enumerate(job["steps"], 1):
            if "run" not in step:
                continue
            shell = (step.get("shell") or default or "bash").split()[0]
            if shell in OTHER_SHELLS:
                continue
            if shell not in ("bash", "sh"):
                problems.append(f"job {name} step {index}: unknown shell {shell!r}")
                continue
            script = EXPRESSION.sub("TSUKI_EXPRESSION", step["run"]).encode("utf-8")
            result = subprocess.run([bash, "-n"], input=script, capture_output=True, timeout=60)
            if result.returncode:
                error = result.stderr.decode("utf-8", "replace").strip()
                problems.append(f"job {name} step {index} ({step.get('name', '')}): bash -n: {error}")
    return problems


@pytest.fixture(scope="module")
def bash():
    found = find_bash()
    if found is None and os.name == "nt":
        pytest.skip("Git for Windows' bash was not found next to git")
    assert found, "bash is needed to check the workflows' run: blocks"
    return found


def test_there_are_workflows_to_check():
    assert {p.name for p in WORKFLOWS} >= {"platforms.yml", "macos-release.yml", "python-security.yml"}


@pytest.mark.parametrize("path", WORKFLOWS, ids=lambda p: p.name)
def test_every_workflow_parses_with_known_keys_and_pinned_actions(path):
    assert structure_problems(read_workflow(path.read_text(encoding="utf-8"))) == []


@pytest.mark.parametrize("path", WORKFLOWS, ids=lambda p: p.name)
def test_every_run_block_passes_bash_n(path, bash):
    assert shell_problems(read_workflow(path.read_text(encoding="utf-8")), bash) == []


def test_the_reader_reads_the_workflow_subset():
    text = (
        "name: Example  # a comment\n"
        "on:\n"
        "  push:\n"
        "  schedule:\n"
        "    - cron: '24 8 * * 1'\n"
        "env:\n"
        "  EMPTY: ''\n"
        "  QUOTED: 'it''s'\n"
        "  DOUBLE: \"a \\\"b\\\"\"\n"
        "jobs:\n"
        "  one:\n"
        "    runs-on: ${{ matrix.os }}\n"
        "    strategy:\n"
        "      matrix:\n"
        "        os: [ubuntu-latest, 'windows-latest']\n"
        "    steps:\n"
        "      - uses: actions/checkout@0123456789abcdef0123456789abcdef01234567 # v5.1.0\n"
        "        with:\n"
        "          persist-credentials: false\n"
        "      # a comment between steps\n"
        "      - name: Two lines\n"
        "        if: matrix.os != 'x' && runner.os == 'macOS'\n"
        "        run: |\n"
        "          echo \"a: b\"  # stays in the script\n"
        "\n"
        "          echo '#not a comment'\n"
        "      - run: |-\n"
        "          last\n"
        "      - run: echo a#b # comment\n"
    )
    assert read_workflow(text) == {
        "name": "Example",
        "on": {"push": None, "schedule": [{"cron": "24 8 * * 1"}]},
        "env": {"EMPTY": "", "QUOTED": "it's", "DOUBLE": 'a "b"'},
        "jobs": {"one": {
            "runs-on": "${{ matrix.os }}",
            "strategy": {"matrix": {"os": ["ubuntu-latest", "windows-latest"]}},
            "steps": [
                {"uses": "actions/checkout@0123456789abcdef0123456789abcdef01234567", "with": {"persist-credentials": "false"}},
                {"name": "Two lines", "if": "matrix.os != 'x' && runner.os == 'macOS'",
                 "run": "echo \"a: b\"  # stays in the script\n\necho '#not a comment'\n"},
                {"run": "last"},
                {"run": "echo a#b"},
            ],
        }},
    }


@pytest.mark.parametrize("text, message", [
    ("a: b: c\n", "': ' inside a plain value"),
    ("a:\n  b: 1\n   c: 2\n", "unexpected indentation"),
    ("a: 1\na: 2\n", "duplicate key"),
    ("a: 'open\n", "unterminated"),
    ("a: >\n  folded\n", "only | and |-"),
    ("a: &anchor x\n", "at the start of a value"),
    ("a: {b: c}\n", "at the start of a value"),
    ("a:\n- b\n", "sequence at its key's indentation"),
    ("a: x\n\tb: y\n", "tab in the indentation"),
])
def test_the_reader_refuses_what_it_does_not_read(text, message):
    with pytest.raises(WorkflowSyntaxError, match=re.escape(message)):
        read_workflow(text)


def test_the_checks_catch_the_round5_quoting_bug_and_bad_steps(bash):
    # the round-5 smoke line: a stray quote left the run: block unterminated, so the job failed before its upload
    broken = read_workflow(
        "on:\n  workflow_dispatch:\njobs:\n  package:\n    runs-on: macos-15\n    steps:\n"
        "      - name: Smoke\n        run: |\n"
        "          app/TsukihimeLauncher --selftest --data \"${{ matrix.arch }}/profile' --game /nonexistent\n"
        "      - uses: actions/checkout@v5\n"
        "      - name: Typo\n        runs: echo\n"
        "      - uses: actions/upload-artifact@cf430e030ddbb5b0abf93d22962f4752f3646cd9\n"
        "        with:\n          name: ignored\n          path: a.zip\n          archive: false\n"
        "      - run: echo ${{ matrix.arch\n"
    )
    assert any(p.startswith("job package step 1 (Smoke): bash -n") for p in shell_problems(broken, bash))
    problems = structure_problems(broken)
    assert any("actions/checkout@v5 is not pinned" in p for p in problems)
    assert any("unknown key 'runs'" in p for p in problems)
    assert any("needs exactly one of uses and run" in p for p in problems)
    assert any("ignores name with archive: false" in p for p in problems)
    assert any("unclosed ${{ expression" in p for p in problems)


def test_workflows_that_build_opt_out_of_build_telemetry():
    building = 0
    for path in WORKFLOWS:
        workflow = read_workflow(path.read_text(encoding="utf-8"))
        for name, job in workflow["jobs"].items():
            if not any("dotnet " in (step.get("run") or "") for step in job["steps"]):
                continue
            building += 1
            env = {**(workflow.get("env") or {}), **(job.get("env") or {})}
            assert env.get("AVALONIA_TELEMETRY_OPTOUT") == "1", f"{path.name} job {name}"
            assert env.get("DOTNET_CLI_TELEMETRY_OPTOUT") == "1", f"{path.name} job {name}"
    assert building >= 2


def step_index(steps: list, needle: str) -> int:
    found = [i for i, step in enumerate(steps) if needle in (step.get("run") or "")]
    assert len(found) == 1, f"{needle!r} is in {len(found)} steps"
    return found[0]


def test_the_mac_download_records_its_diagnostics_first_and_smokes_the_extracted_app():
    steps = load("macos-release.yml")["jobs"]["package"]["steps"]
    package = step_index(steps, "package_macos.py")
    summary = step_index(steps, "grep -c com.apple.cs")
    checks = step_index(steps, "release-sums")
    extract = step_index(steps, "ditto -x -k")
    smoke = step_index(steps, "--smoke-headless")
    assert "--zip" in steps[package]["run"]
    # written before release-sums, so a refused ZIP still shows what ditto and codesign produced
    assert package < summary < checks <= extract < smoke
    record = steps[summary]["run"]
    # one fenced block, so the summary's lines do not run together as Markdown
    assert record.count("```") == 2 and record.index("```text") < record.index("Source commit")
    for needle in ("GITHUB_STEP_SUMMARY", "grep -c com.apple.cs", "__MACOSX/", "FFmpeg-SOURCE.txt", "Exact source commit",
                   "shasum -a 256", "github.sha", "nasm -v", "clang --version", "xcodebuild -version"):
        assert needle in record, needle
    # the smoke runs the ditto-extracted copy players get, and writes nothing beside the app that was zipped
    run = steps[smoke]["run"]
    assert "artifacts/verify-${{ matrix.arch }}/Tsukihime.app/Contents/MacOS" in run
    assert "artifacts/package-" not in run
    for option in ("--render", "--data", "--report", "--selftest"):
        assert f'{option} "$RUNNER_TEMP/' in run, option
    assert "smoke.txt" in run and "selftest.txt" in run and "GITHUB_STEP_SUMMARY" in run
    # the launcher's window path (its lock, clean-up and main loop), which crashed at every start before cfdb8da
    assert "TsukihimeLauncher\" --window-smoke" in run


def test_the_mac_download_checks_the_version_before_anything_is_built():
    steps = load("macos-release.yml")["jobs"]["package"]["steps"]
    assert str(steps[0].get("uses", "")).startswith("actions/checkout@")
    run = steps[1]["run"]
    assert "python3 -I tools/setup/release_version.py --ref-type \"$GITHUB_REF_TYPE\" --ref-name \"$GITHUB_REF_NAME\"" in run
    # the version is the first line of the job's summary, and the later steps take it from here, not from one project file
    assert "GITHUB_STEP_SUMMARY" in run and 'echo "version=$version" >> "$GITHUB_ENV"' in run
    assert all("csproj" not in (step.get("run") or "") for step in steps)


def test_the_mac_download_keeps_its_record_and_diagnostics_after_a_failure():
    steps = load("macos-release.yml")["jobs"]["package"]["steps"]
    record = steps[step_index(steps, "grep -c com.apple.cs")]
    assert record.get("if") == "always()"
    # after a failure the app or the ZIP may be missing: each is looked at only if it is there
    assert '[ -d "$app" ]' in record["run"] and '[ -f "$zip" ]' in record["run"] and '[ -f "$ffmpeg" ]' in record["run"]
    # the identifier codesign gave each program (mac-bundle-1: the release rules demand exact ones)
    assert "codesign -dv \"$app/Contents/MacOS/$program\" 2>&1 | grep '^Identifier=' || echo none" in record["run"]
    assert "for program in TsukihimeLauncher Tsukihime" in record["run"]
    collect = steps[step_index(steps, "codesign -dvvv --deep")]
    upload = [step for step in steps if (step.get("with") or {}).get("name") == "mac-diagnostics-${{ matrix.arch }}-not-for-release"]
    assert collect.get("if") == "failure()" and len(upload) == 1 and upload[0].get("if") == "failure()"
    assert "artifacts/package-${{ matrix.arch }}" in upload[0]["with"]["path"] and "mac-diagnostics" in upload[0]["with"]["path"]
    assert steps.index(collect) < steps.index(upload[0])


def test_the_mac_download_keeps_the_ffmpeg_configure_log_after_a_failure():
    # mac-2: a configure failure on the first hosted run must keep its log; configure writes ffbuild/config.log where it runs
    script = (REPO / "tools" / "setup" / "ffmpeg_macos.sh").read_text(encoding="utf-8")
    work = re.search(r'^work="\$root/(.+)"$', script, re.M).group(1)
    assert re.search(r'cd "\$work/build"\n\s+"\$build_source/configure"', script)
    log = work.replace("$arch", "${{ matrix.arch }}") + "/build/ffbuild/config.log"
    assert log == "third_party/ffmpeg/macos-${{ matrix.arch }}-build/build/ffbuild/config.log"
    steps = load("macos-release.yml")["jobs"]["package"]["steps"]
    assert f'cp "{log}" "$out/" 2>/dev/null || true' in steps[step_index(steps, "codesign -dvvv --deep")]["run"]


def test_macos_26_keeps_its_diagnostics_after_a_failure():
    # mac-1: the macOS 26 job kept nothing, so a red updater check there showed only the summary
    jobs = load("macos-release.yml")["jobs"]
    steps = jobs["check-macos-26"]["steps"]
    collect = steps[step_index(steps, "codesign -dvvv --deep")]
    upload = [step for step in steps if str(step.get("uses", "")).startswith("actions/upload-artifact@")]
    assert collect.get("if") == "failure()" and len(upload) == 1 and upload[0].get("if") == "failure()"
    assert step_index(steps, "tools/tests/update_e2e.py") < steps.index(collect) < steps.index(upload[0])
    name = upload[0]["with"]["name"]
    # artifact names are unique in a run: not the package job's diagnostics of the same arch
    assert name == "mac26-diagnostics-${{ matrix.arch }}-not-for-release"
    assert name not in {(step.get("with") or {}).get("name") for step in jobs["package"]["steps"]}
    assert "`mac26-diagnostics-ARCH-not-for-release`" in (REPO / "docs" / "PUBLISHING.md").read_text(encoding="utf-8")
    run = collect["run"]
    assert 'out="$RUNNER_TEMP/mac26-diagnostics"' in run and upload[0]["with"]["path"] == "${{ runner.temp }}/mac26-diagnostics"
    for needle in ("for name in verify update-e2e/install", '[ -d "$app" ] || continue', "xattr -lr",
                   'cp "$RUNNER_TEMP"/update-e2e/report.txt* "$RUNNER_TEMP"/update-e2e/profile/*.log "$out/" 2>/dev/null || true'):
        assert needle in run, needle


def test_every_release_publish_of_the_guide_is_a_ready_to_run_build():
    # win-r2r: v0.1.6 shipped a ReadyToRun Windows build, but PUBLISHING 4.1 left it out and called it optional
    text = (REPO / "docs" / "PUBLISHING.md").read_text(encoding="utf-8")
    commands = [re.sub(r"\s*\\\n\s*", " ", command) for command in re.findall(r"^dotnet publish src(?:.*\\\n)*.*", text, re.M)]
    assert {re.search(r" -r (\S+)", command).group(1) for command in commands} == {"win-x64", "linux-x64", "osx-arm64"}
    for command in commands:
        assert "-c Release" in command and "--self-contained" in command and "-p:PublishReadyToRun=true" in command, command
    assert "PublishReadyToRun=true` is optional" not in text
    # "a Release publish already leaves out debug symbols": Directory.Build.props, so the commands need no -p:DebugType=none
    assert "A Release publish already leaves out debug symbols" in text
    props = (REPO / "Directory.Build.props").read_text(encoding="utf-8")
    assert re.search(r"<PropertyGroup Condition=\"'\$\(Configuration\)' == 'Release' and '\$\(_IsPublishing\)' == 'true'\">\s*"
                     r"<DebugType>none</DebugType>\s*<DebugSymbols>false</DebugSymbols>", props)


def test_the_mac_workflows_run_the_real_updater_on_the_real_zip():
    zip_path = "Tsukihime-v${{ env.version }}-macos-${{ matrix.arch }}.zip"
    release = load("macos-release.yml")["jobs"]
    platforms = load("platforms.yml")["jobs"]["build-test"]["steps"]
    for steps, before in ((release["package"]["steps"], "--smoke-headless"), (platforms, "release-sums"),
                          (release["check-macos-26"]["steps"], "--smoke-headless")):
        e2e = step_index(steps, "tools/tests/update_e2e.py")
        run = steps[e2e]["run"]
        assert run.startswith("python3 -I tools/tests/update_e2e.py") and zip_path in run
        assert '--work "$RUNNER_TEMP/update-e2e" --summary "$GITHUB_STEP_SUMMARY"' in run
        assert step_index(steps, before) < e2e
        assert "continue-on-error" not in steps[e2e]
    assert platforms[step_index(platforms, "tools/tests/update_e2e.py")].get("if") == "runner.os == 'macOS'"
    package = release["package"]["steps"]
    uploads = [i for i, step in enumerate(package) if str(step.get("uses", "")).startswith("actions/upload-artifact@") and "if" not in step]
    # after both uploads: a red updater run still leaves its ZIPs to inspect, and the failure diagnostics still see its reports
    assert len(uploads) == 2 and max(uploads) < step_index(package, "tools/tests/update_e2e.py") < step_index(package, "mkdir -p \"$out\"")
    assert release["check-macos-26"]["if"] == "${{ !cancelled() }}"


def test_macos_26_checks_the_same_zips_without_a_rebuild():
    jobs = load("macos-release.yml")["jobs"]
    job = jobs["check-macos-26"]
    assert job["needs"] == "package"
    assert {(row["os"], row["arch"]) for row in job["strategy"]["matrix"]["include"]} == {("macos-26", "arm64"), ("macos-26-intel", "x64")}
    runs = "\n".join(step.get("run") or "" for step in job["steps"])
    assert "dotnet " not in runs and "ffmpeg_macos.sh" not in runs and "package_macos.py" not in runs
    download = [step for step in job["steps"] if str(step.get("uses", "")).startswith("actions/download-artifact@")]
    internal = [step for step in jobs["package"]["steps"] if (step.get("with") or {}).get("name", "").startswith("internal-macos-26-check-")]
    assert len(download) == 1 and len(internal) == 1 and download[0]["with"]["name"] == internal[0]["with"]["name"]
    assert internal[0]["with"].get("archive") is None and internal[0]["with"]["retention-days"] == "1"
    for needle in ("ditto -x -k", "codesign --verify --deep --strict", "--selftest", "--render", "--window-smoke", "--smoke-headless"):
        assert needle in runs, needle


def test_the_gl_window_smoke_is_informational_and_bounded():
    steps = load("macos-release.yml")["jobs"]["package"]["steps"]
    step = steps[step_index(steps, "Tsukihime\" --smoke --report")]
    assert step.get("continue-on-error") == "true" and step.get("timeout-minutes") == "5"
    assert 'exit "$code"' in step["run"] and "GITHUB_STEP_SUMMARY" in step["run"]


def test_push_ci_runs_the_real_linux_update_test():
    steps = load("platforms.yml")["jobs"]["build-test"]["steps"]
    step = steps[step_index(steps, "LinuxUpdateEndToEndTests")]
    assert step.get("if") == "runner.os == 'Linux'"
    run = step["run"]
    assert "-r linux-x64 --self-contained true -p:TsukiBundleFFmpeg=false -o \"$RUNNER_TEMP/linux-publish\"" in run
    assert 'TSUKI_K48_LINUX_PUBLISH="$RUNNER_TEMP/linux-publish"' in run
    # the test skips without a publish; the step fails unless it ran
    assert "Skipped: +0," in run and "grep -Eq" in run
    assert step_index(steps, "dotnet test tests/Tsuki.Tests.Avalonia -warnaserror") < steps.index(step)


def test_push_ci_runs_the_data_and_python_packaging_tests_off_windows():
    workflow = load("platforms.yml")
    assert workflow["env"]["TSUKI_GAME_DIR"] == "/nonexistent"
    steps = workflow["jobs"]["build-test"]["steps"]
    python = [i for i, step in enumerate(steps) if str(step.get("uses", "")).startswith("actions/setup-python@")]
    data = step_index(steps, "dotnet test tests/Tsuki.Tests.Data")
    pytest_step = step_index(steps, "-m pytest")
    # the Data tests' ICNS agreement check starts the Python writer, so Python is set up first
    assert len(python) == 1 and python[0] < data
    for index in (python[0], data, pytest_step):
        assert steps[index].get("if") == "runner.os != 'Windows'"
    for test in ("test_macos_release.py", "test_linux_release.py", "test_repository_files.py", "test_release_tools.py"):
        assert f"tools/tests/{test}" in steps[pytest_step]["run"]
    # push CI makes and checks the real Mac ZIP too, not only the folder
    assert "--zip" in steps[step_index(steps, "package_macos.py")]["run"]
    assert ".zip" in steps[step_index(steps, "release-sums")]["run"]


def test_shipped_texts_check_out_with_lf_on_every_machine():
    git = shutil.which("git")
    if git is None or not (REPO / ".git").exists():
        pytest.skip("not a git checkout")
    listed = subprocess.run(
        [git, "ls-files", "--", "LICENSE", "README.md", "CONTRIBUTING.md", "docs/*.md", "docs/SETUP_GUIDE.txt", "src/*/Licenses/*"],
        cwd=REPO, capture_output=True, text=True, check=True).stdout.split()
    assert {"LICENSE", "docs/SETUP_GUIDE.txt", "src/Tsuki.Launcher/Licenses/SDL3-zlib.txt"} <= set(listed)
    attributes = subprocess.run([git, "check-attr", "eol", "--", *listed], cwd=REPO, capture_output=True, text=True, check=True).stdout
    eol = dict(line.rsplit(": eol: ", 1) for line in attributes.splitlines())
    assert [name for name in listed if eol.get(name) != "lf"] == []
    rules = [line.strip() for line in (REPO / ".gitattributes").read_text(encoding="utf-8").splitlines()
             if line.strip() and not line.lstrip().startswith("#")]
    assert len(rules) == len(set(rules)), "a duplicate .gitattributes rule"


# what src/Tsuki.Distribution.targets and the launcher projects ship from the repository, besides src/*/Licenses/*
SHIPPED_TEXTS = ("LICENSE", "README.md", "CONTRIBUTING.md", "docs/SETUP_GUIDE.md", "docs/SETUP_GUIDE.txt", "docs/THIRD_PARTY.md",
                 "docs/LINUX.md", "docs/MACOS.md")
REFRESH = ("git ls-files -z -- LICENSE '*.md' '*.txt' | xargs -0 rm -f && git ls-files -z --deleted | xargs -0 git checkout --")


def crlf_texts(root: Path) -> list[str]:
    names = [*SHIPPED_TEXTS, *(path.relative_to(root).as_posix() for path in sorted(root.glob("src/*/Licenses/*")))]
    return [name for name in names if b"\r" in (root / name).read_bytes()]


def test_shipped_texts_have_lf_in_this_checkout():
    # .gitattributes applies only to files git writes: a checkout made before the eol=lf rules keeps its CRLF copies, and a publish
    # copies those bytes into the archives (round 8: LICENSE and five licence texts in an older worktree)
    assert len(list(REPO.glob("src/*/Licenses/*"))) >= 10
    assert crlf_texts(REPO) == [], f"these shipped texts have CRLF here; on a clean tree run {REFRESH}, or publish from a fresh clone"


def test_the_lf_check_finds_a_crlf_licence(tmp_path):
    for name in SHIPPED_TEXTS:
        (tmp_path / name).parent.mkdir(parents=True, exist_ok=True)
        (tmp_path / name).write_bytes(b"text\n")
    (tmp_path / "src" / "Tsuki.Launcher" / "Licenses").mkdir(parents=True)
    (tmp_path / "src" / "Tsuki.Launcher" / "Licenses" / "SDL3-zlib.txt").write_bytes(b"zlib\r\n")
    (tmp_path / "src" / "Tsuki.Launcher" / "Licenses" / "MPL-2.0.txt").write_bytes(b"mpl\n")
    assert crlf_texts(tmp_path) == ["src/Tsuki.Launcher/Licenses/SDL3-zlib.txt"]
    (tmp_path / "docs" / "LINUX.md").write_bytes(b"a\r\nb\n")
    assert crlf_texts(tmp_path) == ["docs/LINUX.md", "src/Tsuki.Launcher/Licenses/SDL3-zlib.txt"]


def plain(markdown: str) -> str:
    """A Markdown table cell as the plain-text guide writes it."""
    return " ".join(markdown.replace("`", "").replace("**", "").replace("\\|", "|").split())


def cells(row: str) -> list[str]:
    return [plain(cell) for cell in re.split(r"(?<!\\)\|", row)[1:-1]]


def guide_tables() -> list[tuple[list[str], list[list[str]]]]:
    """The tables of SETUP_GUIDE.md: each one's header and rows, as plain text."""
    lines = (REPO / "docs" / "SETUP_GUIDE.md").read_text(encoding="utf-8").splitlines()
    tables = []
    for i, line in enumerate(lines):
        if line.startswith("| ") and i + 1 < len(lines) and re.fullmatch(r"\|(---\|)+", lines[i + 1]):
            rows = []
            for row in lines[i + 2:]:
                if not row.startswith("| "):
                    break
                rows.append(cells(row))
            tables.append((cells(line), rows))
    return tables


def text_entries(lines: list[str], name: str, labels: list[str]) -> list[dict[str, str]]:
    """The entries of SETUP_GUIDE.txt headed NAME: their "  Label: text" fields, continuation lines joined."""
    entries = []
    for i, line in enumerate(lines):
        if line != name:
            continue
        fields, label = {}, None
        for follow in lines[i + 1:]:
            start = next((candidate for candidate in labels if follow.startswith(f"  {candidate}: ")), None)
            if start is not None:
                label, fields[start] = start, follow[len(start) + 4:]
            elif label is not None and follow.startswith("  "):
                fields[label] += " " + follow.strip()
            else:
                break
        entries.append({key: plain(value) for key, value in fields.items()})
    return entries


def test_the_text_guide_has_every_table_row_of_the_markdown_guide():
    tables = guide_tables()
    assert len(tables) >= 7 and sum(len(rows) for _, rows in tables) >= 80
    text = (REPO / "docs" / "SETUP_GUIDE.txt").read_text(encoding="utf-8").splitlines()
    for header, rows in tables:
        for row in rows:
            assert dict(zip(header[1:], row[1:])) in text_entries(text, row[0], header[1:]), f"SETUP_GUIDE.txt differs on {row[0]!r}"


def test_the_guide_rows_say_what_linux_macos_and_the_steam_deck_do():
    # unix-3: off Windows every start writes last_run.log in its profile, and a Steam Deck launcher leaves fullscreen= to display_mode
    rows = {row[0]: row[-1] for _, rows in guide_tables() for row in rows}
    files = rows["last_run.log, crash.log"]
    assert "PLAY" in files and "on Linux and macOS, of every start" in files and "Launcher-started" not in files
    assert "on a Steam Deck the line stays and display_mode decides" in rows["fullscreen"]
    save_dir = rows["--save-dir <folder>"]
    assert "On Linux and macOS its last_run.log is the run log" in save_dir and "on Windows a run log still needs --log" in save_dir
