"""Contact sheets for the gallery document (CG thumbnail next to the full image, flowchart
node thumbnails with their titles).  Used by ``python -m tsuki.misc_gallery_extras_flowchart
export --sheets``; reads the game folder only."""
from __future__ import annotations

from pathlib import Path
from typing import List, Sequence, Tuple

ALLPAC3_CG_TH = "GALLERY_PARTS_CG_TH.MRG"
ALLPAC3_FC_TH = "GALLERY_PARTS_FC_TH.MRG"


def pick_cg(entries: Sequence[object], n: int = 20) -> List[object]:
    """``n`` entries spread over the list: every k-th, plus the first special and the first
    alias-unlocked entry so both kinds appear."""
    step = max(1, len(entries) // n)
    return [entries[i] for i in range(0, len(entries), step)][:n]


def _fit(img: "object", w: int, h: int) -> "object":
    from PIL import Image
    im = img.copy()
    im.thumbnail((w, h), Image.LANCZOS)
    return im


def make_sheets(out: Path, image_dir: Path) -> List[Path]:
    from PIL import Image, ImageDraw
    from . import misc_gallery_extras_flowchart as gx
    from .catalog import IMAGE_SEARCH_ORDER, ImageNameIndex, unpack_image_id
    from .image import decode_image, load
    from .mrg import MrgArchive
    from .mzp import MzpArchive
    from .nam import load_nam
    from eboot import eboot_path

    ep = eboot_path()
    if ep is None:
        raise FileNotFoundError("eboot.bin")
    d = gx.load(ep)
    ui = gx.load_ui_text(ep, "en")
    nam = ImageNameIndex({a: load_nam(image_dir / f"{a}.nam") for a in IMAGE_SEARCH_ORDER})
    arcs = {a: MrgArchive(image_dir / a) for a in IMAGE_SEARCH_ORDER}
    written: List[Path] = []
    try:
        a3 = arcs["allpac3"]
        cg_th = MzpArchive(a3.read(a3.find(ALLPAC3_CG_TH).index))
        fc_th = MzpArchive(a3.read(a3.find(ALLPAC3_FC_TH).index))
        # CG sheet: thumbnail | full image | label --------------------------------------------
        rows: List[Tuple[object, object, str]] = []
        for e in pick_cg(d.cg_entries, 20):
            th = Image.fromarray(decode_image(cg_th.child(e.cg_th)).rgba)
            pid = nam.find(e.name)
            if pid is None:
                full = Image.new("RGBA", (16, 9), (255, 0, 255, 255))
                where = "not found"
            else:
                an, idx = unpack_image_id(pid)
                full = Image.fromarray(load(arcs[an], idx).rgba)
                where = f"{an}:{idx}"
            label = (f"g{e.group}/{e.index} {e.prefix!r}{e.name} th={e.cg_th} cat={e.category}\n"
                     f"{where} {full.width}x{full.height} rect=({e.x},{e.y},{e.w},{e.h})")
            rows.append((th, full, label))
        cw, fw, rh = 240, 400, 240
        sheet = Image.new("RGBA", (2 * (cw + fw + 340), 10 * rh), (24, 24, 32, 255))
        dr = ImageDraw.Draw(sheet)
        for i, (th, full, label) in enumerate(rows):
            x0 = (i // 10) * (cw + fw + 340)
            y0 = (i % 10) * rh
            t = _fit(th, cw - 8, rh - 8)
            f = _fit(full, fw - 8, rh - 8)
            sheet.alpha_composite(t, (x0 + 4, y0 + 4))
            sheet.alpha_composite(f, (x0 + cw + 4, y0 + 4))
            dr.multiline_text((x0 + cw + fw + 6, y0 + 8), label, fill=(230, 230, 230, 255))
        p = out / "cg_contact_sheet.png"
        sheet.convert("RGB").save(p)
        written.append(p)
        # flowchart sheet: 24 scene nodes, thumbnail + title --------------------------------------
        nodes = [n for n in d.nodes if n.scene > 0]
        pick = [nodes[i] for i in range(0, len(nodes), max(1, len(nodes) // 24))][:24]
        tw, thh = 300, 200
        sheet = Image.new("RGBA", (6 * tw, 4 * (thh + 40)), (24, 24, 32, 255))
        dr = ImageDraw.Draw(sheet)
        for i, n in enumerate(pick):
            im = Image.fromarray(decode_image(fc_th.child(n.fc_th)).rgba)
            x0, y0 = (i % 6) * tw, (i // 6) * (thh + 40)
            sheet.alpha_composite(_fit(im, tw - 8, thh - 8), (x0 + 4, y0 + 4))
            title = ui[n.title_ui] if 0 < n.title_ui < len(ui) else "?"
            dr.text((x0 + 4, y0 + thh), f"scene {n.scene} th {n.fc_th} col {n.column}", fill=(200, 200, 255, 255))
            dr.text((x0 + 4, y0 + thh + 14), title[:40], fill=(230, 230, 230, 255))
        p = out / "flowchart_contact_sheet.png"
        sheet.convert("RGB").save(p)
        written.append(p)
    finally:
        for a in arcs.values():
            a.close()
    return written
