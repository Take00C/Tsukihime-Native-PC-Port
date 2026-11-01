"""The environment gate refuses unsafe setups before importing tool packages."""
from __future__ import annotations

import importlib.util
from pathlib import Path

import pytest


@pytest.fixture
def checker(monkeypatch, tmp_path):
    path = Path(__file__).resolve().parents[1] / "setup" / "check_python.py"
    spec = importlib.util.spec_from_file_location("check_python", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    monkeypatch.setattr(module.sys, "version_info", (3, 14, 10, "final", 0))
    monkeypatch.setattr(module.sys, "prefix", str(tmp_path))
    monkeypatch.setattr(module.sys, "base_prefix", str(tmp_path / "base"))
    (tmp_path / "pyvenv.cfg").write_text("include-system-site-packages = false\n")
    pins = {}
    for line in (path.parents[2] / "requirements-dev.txt").read_text().splitlines():
        name, _, version = line.partition(";")[0].strip().partition("==")
        pins[name] = version
    monkeypatch.setattr(module.md, "version", pins.__getitem__)
    return module


@pytest.mark.parametrize("version", [
    (3, 11, 9, "final", 0), (3, 14, 8, "final", 0), (3, 14, 9, "final", 0),
    (3, 14, 10, "candidate", 1),
])
def test_old_interpreters_are_rejected(checker, monkeypatch, version):
    monkeypatch.setattr(checker.sys, "version_info", version)
    assert any("Python 3.14.10" in e for e in checker.security_errors())


def test_reviewed_environment_passes(checker):
    assert checker.security_errors() == []


def test_newer_patch_passes(checker, monkeypatch):
    monkeypatch.setattr(checker.sys, "version_info", (3, 14, 11, "final", 0))
    assert checker.security_errors() == []


def test_system_package_inheritance_is_rejected(checker, tmp_path):
    (tmp_path / "pyvenv.cfg").write_text("include-system-site-packages = true\n")
    assert any("--system-site-packages" in e for e in checker.security_errors())


def test_global_interpreter_is_rejected(checker, monkeypatch):
    monkeypatch.setattr(checker.sys, "prefix", checker.sys.base_prefix)
    assert any("isolated .venv" in e for e in checker.security_errors())


@pytest.mark.parametrize("missing", [False, True])
def test_old_or_missing_pillow_is_rejected(checker, monkeypatch, missing):
    original = checker.md.version

    def version(name):
        if name == "pillow":
            if missing:
                raise checker.md.PackageNotFoundError(name)
            return "10.0.0"
        return original(name)

    monkeypatch.setattr(checker.md, "version", version)
    assert any("pillow" in e for e in checker.security_errors())


def test_unsafe_environment_is_not_imported(checker, monkeypatch, capsys):
    monkeypatch.setattr(checker, "security_errors", lambda: ["unsafe environment"])

    def unexpected_import(*args):
        pytest.fail("unsafe packages must not be imported")

    monkeypatch.setattr(checker, "probe_import", unexpected_import)
    assert checker.main() == 1
    assert "unsafe environment" in capsys.readouterr().out
