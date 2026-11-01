// The words the PC port adds in the player's language (package K20). The PS4's own strings come from ui_text (UiContext.Message); these are the ones the game never
// had: the first-run folder prompt, the start-up errors, the save-failure and single-instance notices, the dialog buttons the Japanese sheet does not translate, and the
// texts of the screenshot and clipboard features that package K21 adds. Every entry has the four selectable languages; a test checks that none is blank and that none
// shows the short project name. "{0}" and "{1}" are filled by string.Format with the invariant culture.
using System.Globalization;
using Tsuki.Engine.Contracts;

namespace Tsuki.Ui;

/// <summary>A string the port shows that the PS4 did not have.</summary>
public enum PortString
{
    /// <summary>The window and message-box title of the game program: "Tsukihime".</summary>
    ProductName,

    /// <summary>The first run has no game folder: what the next dialog asks for ({0} is the folder name CUSA47999).</summary>
    FolderPrompt,

    /// <summary>The caption of the folder dialog.</summary>
    FolderDialogTitle,

    /// <summary>The chosen folder holds no eboot.bin.</summary>
    FolderNotTheGame,

    /// <summary>The remembered folder is gone ({0} the folder).</summary>
    FolderGone,

    /// <summary>The remembered folder lacks a file ({0} the file, {1} the folder).</summary>
    FolderIncomplete,

    /// <summary>The remembered folder cannot be opened ({0} the folder, {1} the system's words).</summary>
    FolderUnreadable,

    /// <summary>The second line under a folder error: choose the folder again.</summary>
    ChooseFolderAgain,

    /// <summary>Another game runs on the same profile.</summary>
    AlreadyRunning,

    /// <summary>The save file could not be written ({0} the path, {1} the system's words).</summary>
    SaveFailed,

    /// <summary>The save file was damaged and the backup was used ({0} the backup's name, {1} where the damaged file went).</summary>
    SaveDamagedBackupUsed,

    /// <summary>The save file was damaged and a new profile started ({0} the error, {1} where the damaged file went).</summary>
    SaveDamagedNewProfile,

    /// <summary>The FFmpeg DLLs are missing ({0} the list).</summary>
    FfmpegMissing,

    /// <summary>The graphics driver gave no OpenGL 3.3 context.</summary>
    NoOpenGl,

    /// <summary>The window could not be created.</summary>
    NoWindow,

    /// <summary>The log file could not be opened ({0} the path, {1} the system's words).</summary>
    LogNotOpened,

    /// <summary>"Details: {0}" under an error, naming crash.log.</summary>
    Details,

    /// <summary>The Yes button of a two-button dialog where the game's own sheet is not in the language.</summary>
    DialogYes,

    /// <summary>The No button of a two-button dialog where the game's own sheet is not in the language.</summary>
    DialogNo,

    /// <summary>Package K21: a screenshot was written (K24: the short notice names no file; the log has the path).</summary>
    ScreenshotSaved,

    /// <summary>Package K21: the screenshot could not be written (K24: the short notice carries no error; the log has the system's words).</summary>
    ScreenshotFailed,

    /// <summary>Package K21: the message text was put on the clipboard.</summary>
    TextCopied,

    /// <summary>Package K23: the text WebSocket is listening ({0} the port).</summary>
    TextWebSocketListening,

    /// <summary>Package K23: the text WebSocket could not use its port ({0} the port, {1} the system's words).</summary>
    TextWebSocketFailed,

    /// <summary>Package K25: the chosen path is a .pkg file ({0} the file, {1} the folder name CUSA47999).</summary>
    FolderIsPkg,

    /// <summary>Package K25: the chosen folder holds only the update ({0} the folder).</summary>
    FolderIsUpdateOnly,

    /// <summary>Package K25: the chosen folder holds several games ({0} the folder, {1} their names).</summary>
    FolderHoldsSeveral,

    /// <summary>Package K25: the chosen folder holds no game, not even one or two levels down ({0} the folder).</summary>
    FolderNoGameInside,

    /// <summary>Package K25: the eboot is a build the port does not know and tables are missing ({0} the build's short name, {1} the start of the eboot's SHA-256, {2} the table names).</summary>
    BuildUnsupported,

    /// <summary>Package K25: the toast / log line for a copy that is not the verified build but runs ({0} the build's description).</summary>
    BuildOtherVersion,

    /// <summary>The Linux FFmpeg 9 shared libraries are missing.</summary>
    LinuxFfmpegMissing,
}

/// <summary>The port's own strings in ja, en, zc and zt.</summary>
public static class PortText
{
    /// <summary>Every string, in the order of <see cref="PortString"/>; each row is ja, en, zc, zt.</summary>
    private static readonly string[][] Table =
    [
        // ProductName
        ["Tsukihime", "Tsukihime", "Tsukihime", "Tsukihime"],
        // FolderPrompt
        [
            "Tsukihime を遊ぶには、お手持ちの PS4 版のゲームデータが必要です。\n\n次のウィンドウで、展開した {0} フォルダ（eboot.bin と image\\ が入っているフォルダ）を選んでください。フォルダの中身は読み込むだけで、変更されることはありません。",
            "Tsukihime needs your own copy of the PS4 game.\n\nIn the next window, choose the extracted {0} folder (the one that holds eboot.bin and image\\). The folder is only read; nothing in it is ever changed.",
            "运行 Tsukihime 需要您自己的 PS4 版游戏数据。\n\n请在下一个窗口中选择解压后的 {0} 文件夹（其中包含 eboot.bin 和 image\\）。该文件夹只会被读取，不会被修改。",
            "執行 Tsukihime 需要您自己的 PS4 版遊戲資料。\n\n請在下一個視窗中選擇解壓後的 {0} 資料夾（其中包含 eboot.bin 和 image\\）。該資料夾只會被讀取，不會被修改。",
        ],
        // FolderDialogTitle
        ["Tsukihime: CUSA47999 フォルダを選んでください", "Tsukihime: choose the CUSA47999 folder", "Tsukihime: 请选择 CUSA47999 文件夹", "Tsukihime: 請選擇 CUSA47999 資料夾"],
        // FolderNotTheGame
        [
            "そのフォルダには eboot.bin がありません。ゲームの CUSA47999 フォルダを選んでください。",
            "That folder does not contain eboot.bin. Choose the CUSA47999 folder of the game.",
            "该文件夹中没有 eboot.bin。请选择游戏的 CUSA47999 文件夹。",
            "該資料夾中沒有 eboot.bin。請選擇遊戲的 CUSA47999 資料夾。",
        ],
        // FolderGone
        [
            "ゲームフォルダ {0} が見つかりません。",
            "The game folder {0} no longer exists.",
            "找不到游戏文件夹 {0}。",
            "找不到遊戲資料夾 {0}。",
        ],
        // FolderIncomplete
        [
            "ゲームフォルダが不完全です: {1} に {0} がありません。",
            "The game folder is incomplete: {0} is missing from {1}.",
            "游戏文件夹不完整: {1} 中缺少 {0}。",
            "遊戲資料夾不完整: {1} 中缺少 {0}。",
        ],
        // FolderUnreadable
        [
            "ゲームフォルダ {0} を開けませんでした: {1}",
            "The game folder {0} could not be opened: {1}",
            "无法打开游戏文件夹 {0}: {1}",
            "無法開啟遊戲資料夾 {0}: {1}",
        ],
        // ChooseFolderAgain
        [
            "ランチャーで CUSA47999 フォルダをもう一度選んでください。",
            "Choose the CUSA47999 folder again in the launcher.",
            "请在启动器中重新选择 CUSA47999 文件夹。",
            "請在啟動器中重新選擇 CUSA47999 資料夾。",
        ],
        // AlreadyRunning
        ["Tsukihime はすでに起動しています。", "Tsukihime is already running.", "Tsukihime 已经在运行。", "Tsukihime 已經在執行。"],
        // SaveFailed
        [
            "セーブファイルを書き込めませんでした:\n{0}\n\n{1}\n\n今回のセーブはゲームを閉じるまでしか残りません。フォルダの空き容量や権限を確認するか、ランチャーで別のフォルダを選んでください。",
            "The save file could not be written:\n{0}\n\n{1}\n\nSaves made now are kept until the game closes only. Free the folder or choose another one in the launcher.",
            "无法写入存档文件:\n{0}\n\n{1}\n\n现在的存档只会保留到游戏关闭为止。请检查该文件夹的空间和权限，或在启动器中选择另一个文件夹。",
            "無法寫入存檔檔案:\n{0}\n\n{1}\n\n現在的存檔只會保留到遊戲關閉為止。請檢查該資料夾的空間和權限，或在啟動器中選擇另一個資料夾。",
        ],
        // SaveDamagedBackupUsed
        [
            "セーブファイルが壊れていたため、ひとつ前の世代 ({0}) を使いました。その後に作ったセーブは失われているかもしれません。\n\n壊れたファイルは次の場所に移しました:\n{1}",
            "Your save file was damaged, so the previous generation ({0}) was used instead; the saves made after it may be gone.\n\nThe damaged file was moved to\n{1}",
            "存档文件已损坏，因此改用了上一代存档 ({0})；在那之后的存档可能已经丢失。\n\n损坏的文件已移至\n{1}",
            "存檔檔案已損壞，因此改用了上一代存檔 ({0})；在那之後的存檔可能已經遺失。\n\n損壞的檔案已移至\n{1}",
        ],
        // SaveDamagedNewProfile
        [
            "セーブファイルが壊れていました ({0})。\n\nファイルは次の場所に移し、\n{1}\n新しいプロファイルを始めました。ランチャーの「Saves」ページからバックアップを復元できます。",
            "Your save file was damaged ({0}).\n\nIt was moved to\n{1}\nand a new profile was started. The launcher's Saves page can restore a backup.",
            "存档文件已损坏 ({0})。\n\n文件已移至\n{1}\n并已开始新的配置文件。启动器的 Saves 页面可以恢复备份。",
            "存檔檔案已損壞 ({0})。\n\n檔案已移至\n{1}\n並已開始新的設定檔。啟動器的 Saves 頁面可以還原備份。",
        ],
        // FfmpegMissing
        [
            "Tsukihime.exe と同じフォルダに FFmpeg のライブラリがありません ({0})。ポートを入れ直すか、ファイルを戻してください。",
            "The FFmpeg libraries are missing next to Tsukihime.exe ({0}). Reinstall the port or copy them back.",
            "Tsukihime.exe 旁边缺少 FFmpeg 库 ({0})。请重新安装移植版或把文件复制回来。",
            "Tsukihime.exe 旁邊缺少 FFmpeg 程式庫 ({0})。請重新安裝移植版或把檔案複製回來。",
        ],
        // NoOpenGl
        [
            "グラフィックドライバーが OpenGL 3.3 に対応していません。ドライバーを更新するか、専用 GPU でゲームを起動してください。",
            "Your graphics driver does not provide OpenGL 3.3. Update the driver or run the game on the dedicated GPU.",
            "您的显卡驱动不支持 OpenGL 3.3。请更新驱动，或在独立显卡上运行游戏。",
            "您的顯示卡驅動程式不支援 OpenGL 3.3。請更新驅動程式，或在獨立顯示卡上執行遊戲。",
        ],
        // NoWindow
        [
            "ゲームウィンドウを作成できませんでした。グラフィックドライバーを更新するか、専用 GPU でゲームを起動してください。",
            "The game window could not be created. Update your graphics driver or run the game on the dedicated GPU.",
            "无法创建游戏窗口。请更新显卡驱动，或在独立显卡上运行游戏。",
            "無法建立遊戲視窗。請更新顯示卡驅動程式，或在獨立顯示卡上執行遊戲。",
        ],
        // LogNotOpened
        [
            "ログファイル {0} を開けませんでした: {1}\n\nログなしでゲームを続けます。",
            "The log file {0} could not be opened: {1}\n\nThe game goes on without a log file.",
            "无法打开日志文件 {0}: {1}\n\n游戏将在没有日志文件的情况下继续。",
            "無法開啟記錄檔 {0}: {1}\n\n遊戲將在沒有記錄檔的情況下繼續。",
        ],
        // Details
        ["詳細: {0}", "Details: {0}", "详情: {0}", "詳情: {0}"],
        // DialogYes
        ["はい", "Yes", "是", "是"],
        // DialogNo
        ["いいえ", "No", "否", "否"],
        // ScreenshotSaved
        ["スクリーンショットを保存しました", "Screenshot saved", "已保存截图", "已儲存螢幕截圖"],
        // ScreenshotFailed
        ["スクリーンショットを保存できませんでした", "The screenshot could not be saved", "无法保存截图", "無法儲存螢幕截圖"],
        // TextCopied
        ["テキストをクリップボードにコピーしました", "Text copied to the clipboard", "已将文本复制到剪贴板", "已將文字複製到剪貼簿"],
        // TextWebSocketListening
        ["テキスト WebSocket: ws://127.0.0.1:{0}", "Text WebSocket: ws://127.0.0.1:{0}", "文本 WebSocket: ws://127.0.0.1:{0}", "文字 WebSocket: ws://127.0.0.1:{0}"],
        // TextWebSocketFailed
        [
            "テキスト WebSocket をポート {0} で開始できませんでした: {1}",
            "The text WebSocket could not start on port {0}: {1}",
            "无法在端口 {0} 启动文本 WebSocket: {1}",
            "無法在連接埠 {0} 啟動文字 WebSocket: {1}",
        ],
        // FolderIsPkg
        [
            "{0} は PS4 のパッケージファイル (.pkg) です。先に shadPS4 や PKG 展開ツールで展開し、展開された {1} フォルダ（eboot.bin と image\\ が入っているフォルダ）を選んでください。",
            "{0} is a PS4 package file (.pkg). Extract it first (shadPS4, or a PKG extractor), then choose the extracted {1} folder (the one that holds eboot.bin and image\\).",
            "{0} 是 PS4 的安装包文件 (.pkg)。请先用 shadPS4 或 PKG 解包工具解压，然后选择解压后的 {1} 文件夹（其中包含 eboot.bin 和 image\\）。",
            "{0} 是 PS4 的安裝包檔案 (.pkg)。請先用 shadPS4 或 PKG 解包工具解壓，然後選擇解壓後的 {1} 資料夾（其中包含 eboot.bin 和 image\\）。",
        ],
        // FolderIsUpdateOnly
        [
            "{0} にはアップデート（パッチ）しか入っていません。ゲーム本体のフォルダ（image\\allpac0.mrg などのアーカイブが入っているフォルダ）を、そのフォルダの隣に置いてください。",
            "{0} holds only the game's update (patch). The base game folder (the one with image\\allpac0.mrg and the other archives) must be beside it.",
            "{0} 中只有游戏的更新（补丁）。游戏本体的文件夹（含 image\\allpac0.mrg 等档案的文件夹）必须放在它旁边。",
            "{0} 中只有遊戲的更新（修補程式）。遊戲本體的資料夾（含 image\\allpac0.mrg 等檔案的資料夾）必須放在它旁邊。",
        ],
        // FolderHoldsSeveral
        [
            "{0} には複数のゲームフォルダがあります ({1})。そのうちのひとつを選んでください。",
            "{0} holds several game folders ({1}). Choose one of them.",
            "{0} 中有多个游戏文件夹 ({1})。请选择其中一个。",
            "{0} 中有多個遊戲資料夾 ({1})。請選擇其中一個。",
        ],
        // FolderNoGameInside
        [
            "{0} にはゲームがありません: eboot.bin と image\\ を含むフォルダが、その中にも見つかりませんでした。",
            "{0} holds no game: no folder with eboot.bin and image\\ was found in it, not even one or two levels down.",
            "{0} 中没有游戏: 其中（包括下一两级子文件夹）没有同时包含 eboot.bin 和 image\\ 的文件夹。",
            "{0} 中沒有遊戲: 其中（包括下一兩級子資料夾）沒有同時包含 eboot.bin 和 image\\ 的資料夾。",
        ],
        // BuildUnsupported
        [
            "このゲームデータ ({0}、eboot.bin SHA-256 {1}...) は、このポートが知らないビルドです。ゲーム本体の中にあるべきテーブルのうち、次のものが見つかりませんでした: {2}。\n\nログファイルを添えて報告してください。",
            "This copy of the game ({0}, eboot.bin SHA-256 {1}...) is a build the port does not know, and these tables could not be found in its program: {2}.\n\nPlease report it together with the log file.",
            "这份游戏数据 ({0}，eboot.bin SHA-256 {1}...) 是本移植版未知的版本，其程序中有下列表未能找到: {2}。\n\n请连同日志文件一起报告。",
            "這份遊戲資料 ({0}，eboot.bin SHA-256 {1}...) 是本移植版未知的版本，其程式中有下列表未能找到: {2}。\n\n請連同記錄檔一起回報。",
        ],
        // BuildOtherVersion
        [
            "検証済みのビルドではありません: {0}。テーブルは内容から探し出されました。",
            "Not the verified build: {0}. The tables were located by their content.",
            "不是经过验证的版本: {0}。表已按内容定位。",
            "不是經過驗證的版本: {0}。表已按內容定位。",
        ],
        // LinuxFfmpegMissing
        [
            "FFmpeg 9 の共有ライブラリがありません ({0})。FFmpeg 9 の共有ライブラリをインストールするか、Tsukihime と同じフォルダに配置してください。",
            "FFmpeg 9 shared libraries are missing ({0}). Install the FFmpeg 9 shared libraries or place them next to Tsukihime.",
            "缺少 FFmpeg 9 共享库 ({0})。请安装 FFmpeg 9 共享库，或将其放在 Tsukihime 旁边。",
            "缺少 FFmpeg 9 共用程式庫 ({0})。請安裝 FFmpeg 9 共用程式庫，或將其放在 Tsukihime 旁邊。",
        ],
    ];

    /// <summary>The selectable languages, in the order of the table's columns.</summary>
    public static IReadOnlyList<Language> Languages { get; } = [Language.Ja, Language.En, Language.Zc, Language.Zt];

    /// <summary>The string in <paramref name="language"/> (English for Korean and any value outside the four).</summary>
    public static string Get(PortString id, Language language)
    {
        int row = (int)id;
        if (row < 0 || row >= Table.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(id), id, "not a port string");
        }

        return Table[row][Column(language)];
    }

    /// <summary>The string with its placeholders filled (invariant culture).</summary>
    public static string Format(PortString id, Language language, params object?[] args) =>
        string.Format(CultureInfo.InvariantCulture, Get(id, language), args);

    /// <summary>
    /// The language of a Windows UI culture name for a profile that has none yet (the first run: nothing in save.dat or settings.ini says what the player reads):
    /// Japanese, Simplified Chinese (zh-Hans, zh-CN, zh-SG), Traditional Chinese (zh-Hant, zh-TW, zh-HK, zh-MO), else English.
    /// </summary>
    public static Language FromCultureName(string? cultureName)
    {
        if (string.IsNullOrEmpty(cultureName))
        {
            return Language.En;
        }

        if (cultureName.StartsWith("ja", StringComparison.OrdinalIgnoreCase))
        {
            return Language.Ja;
        }

        if (cultureName.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            return cultureName.Contains("Hant", StringComparison.OrdinalIgnoreCase)
                || cultureName.EndsWith("-TW", StringComparison.OrdinalIgnoreCase)
                || cultureName.EndsWith("-HK", StringComparison.OrdinalIgnoreCase)
                || cultureName.EndsWith("-MO", StringComparison.OrdinalIgnoreCase)
                ? Language.Zt
                : Language.Zc;
        }

        return Language.En;
    }

    private static int Column(Language language) => language switch
    {
        Language.Ja => 0,
        Language.Zc => 2,
        Language.Zt => 3,
        _ => 1,
    };
}
