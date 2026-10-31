"""The misc_adv_textbox_layout alias re-exports tsuki.textbox (tests in test_textbox.py)."""
from __future__ import annotations

from tsuki import misc_adv_textbox_layout as alias
from tsuki import textbox


def test_alias_reexports_textbox() -> None:
    assert set(alias.__all__) == set(textbox.__all__)
    assert alias.layout_box is textbox.layout_box and alias.classify is textbox.classify
