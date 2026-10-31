"""Writes the 3072-pixel BGRA test pattern of the STEC golden (stec_emu.patterns, seed 7) to the path in argv[1]."""
import sys

from tsuki import misc_compositing_transitions_shaders as M

with open(sys.argv[1], "wb") as f:
    f.write(M.stec_test_pattern().tobytes())
