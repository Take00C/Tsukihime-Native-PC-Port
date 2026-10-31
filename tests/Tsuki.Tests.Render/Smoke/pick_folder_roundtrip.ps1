# Manual check of the native folder dialog of "Tsukihime --pick-folder" (PACE 7.2 #1, H1; ARCHITECTURE 4.1).
# Not part of the automatic test run: it opens a real dialog and takes the keyboard focus for about a second.
#   pwsh -NoProfile -File tests/Tsuki.Tests.Render/Smoke/pick_folder_roundtrip.ps1 -Exe <Tsukihime.exe> -Target D:\TsukihimePort\docs\status -Out <file>            # expect exit 0, "chosen: <Target>"
#   pwsh -NoProfile -File tests/Tsuki.Tests.Render/Smoke/pick_folder_roundtrip.ps1 -Exe <Tsukihime.exe> -Target "" -Out <file> -Button 2                       # expect exit 2, "cancelled"
param([string]$Exe, [string]$Target, [string]$Out, [int]$Button = 1)
# Opens Tsukihime --pick-folder, types $Target into the dialog's folder box with WM_SETTEXT and
# presses button $Button (1 = Select Folder, 2 = Cancel) with BM_CLICK. Every wait is bounded.
Add-Type @"
using System; using System.Text; using System.Runtime.InteropServices;
public static class W32 {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc f, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr p, EnumProc f, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetDlgItem(IntPtr h, int id);
  [DllImport("user32.dll")] public static extern int GetDlgCtrlID(IntPtr h);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, string l);
  [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  public static IntPtr Dialog(uint pid) { IntPtr r = IntPtr.Zero; EnumWindows((h,l) => { uint p; GetWindowThreadProcessId(h, out p); var c = new StringBuilder(64); GetClassName(h, c, 64); if (p == pid && IsWindowVisible(h) && c.ToString() == "#32770") { r = h; return false; } return true; }, IntPtr.Zero); return r; }
  public static IntPtr FolderEdit(IntPtr dlg) { IntPtr r = IntPtr.Zero; EnumChildWindows(dlg, (h,l) => { var c = new StringBuilder(64); GetClassName(h, c, 64); if (c.ToString() == "Edit" && IsWindowVisible(h)) { IntPtr parent = h; r = h; } return true; }, IntPtr.Zero); return r; }
}
"@
$p = Start-Process -FilePath $Exe -ArgumentList "--pick-folder","--timeout","30" -RedirectStandardOutput $Out -PassThru
$dlg = [IntPtr]::Zero
for ($i = 0; $i -lt 50 -and $dlg -eq [IntPtr]::Zero; $i++) { Start-Sleep -Milliseconds 200; $dlg = [W32]::Dialog([uint32]$p.Id) }
if ($dlg -eq [IntPtr]::Zero) { "dialog not found"; if (-not $p.HasExited) { $p.Kill() }; exit 1 }
"dialog hwnd $dlg"
Start-Sleep -Milliseconds 500
# the "Folder:" box: ComboBoxEx32 id 1152 -> ComboBox -> Edit
$combo = [W32]::GetDlgItem($dlg, 1152)
$edit = [IntPtr]::Zero
if ($combo -ne [IntPtr]::Zero) { [W32]::EnumChildWindows($combo, { param($h, $l) $c = New-Object Text.StringBuilder 64; [W32]::GetClassName($h, $c, 64) | Out-Null; if ($c.ToString() -eq "Edit") { $script:edit = $h }; $true }, [IntPtr]::Zero) | Out-Null }
if ($edit -eq [IntPtr]::Zero) { $edit = [W32]::FolderEdit($dlg) }
"combo $combo edit $edit"
[W32]::SendMessage($edit, 0x000C, [IntPtr]::Zero, $Target) | Out-Null   # WM_SETTEXT
$okButton = [W32]::GetDlgItem($dlg, $Button)
"ok button $okButton"
[W32]::SendMessage($okButton, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null   # BM_CLICK
$p.WaitForExit(20000) | Out-Null
if (-not $p.HasExited) { $p.Kill(); "killed" }
"exited=$($p.HasExited) code=$($p.ExitCode)"
Get-Content $Out
