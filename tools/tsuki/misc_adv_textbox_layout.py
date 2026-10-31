"""Alias of :mod:`tsuki.textbox` (ADV message window and main text box).

The workflow task for the "adv_textbox_layout" gap names both ``tsuki.textbox`` and this
module; the implementation lives in ``tools/tsuki/textbox.py`` and the documentation in
``docs/formats/textbox.md``.  Everything public is re-exported here.
"""
from __future__ import annotations

from .textbox import *  # noqa: F401,F403
from .textbox import __all__ as _textbox_all

__all__ = list(_textbox_all)
