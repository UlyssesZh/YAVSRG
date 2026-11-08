open System
open System.IO
open System.Threading
open System.Diagnostics
open Percyqaz.Common
open Percyqaz.Shell
open Percyqaz.Flux
open Percyqaz.Flux.Windowing
open Prelude
open Interlude
open Interlude.Resources
open Interlude.Options
open Interlude.UI

let crash_and_open_text_file (message: string) : unit =
    let path = Path.ChangeExtension(Path.GetTempFileName(), ".txt")
    File.WriteAllText(path, message)
    open_directory(path)

let launch_new_instance (instance: int) : unit =
    Logging.Verbosity <- if DEV_MODE then LoggingLevel.DEBUG else LoggingLevel.INFO

    Logging.LogFile <- Some(Path.Combine(get_game_folder "Logs", sprintf "log-%s.txt" (DateTime.Today.ToString("yyyyMMdd"))))

    if OperatingSystem.IsWindows() then
        Process.GetCurrentProcess().PriorityClass <- ProcessPriorityClass.High

    let show_crash_splash =
        EmbeddedResource.SplashMessageGenerator("CrashSplashes.txt") >> Logging.Critical("%s")

    let init () =
        let ui_root = Startup.init instance
        AppDomain.CurrentDomain.ProcessExit.Add(fun _ -> Startup.deinit Startup.ExternalCrash show_crash_splash)
        ui_root :> UIEntryPoint

    let icon =
        use icon_stream = EmbeddedResource.GetStream("icon.png")
        Bitmap.from_stream true icon_stream

    let result =
        Launch.entry_point(Options.load_window_config instance, Updates.version, init, icon)

    Startup.deinit (if result = Ok() then Startup.Normal else Startup.InternalCrash) show_crash_splash

let send_ipc_message (message: string) : unit =
    match Shell.IPC.send "Interlude" message with
    | Some success -> printfn "%s" success
    | None -> printfn "Error: Connection timed out!"

[<EntryPoint>]
let main (argv: string array) : int =
    let executable_location = AppDomain.CurrentDomain.BaseDirectory
    let game_dir =
        match Environment.GetEnvironmentVariable("INTERLUDE_PATH_OVERRIDE") with
        | null ->
            try
                // Probe with a unique file so existing game files are never overwritten.
                use probe =
                    new FileStream(
                        Path.Combine(executable_location, Path.GetRandomFileName()),
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        1,
                        FileOptions.DeleteOnClose
                    )
                probe.WriteByte(0uy)
                executable_location
            with
            | :? UnauthorizedAccessException
            | :? IOException ->
                let data_home =
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create)
                Path.Combine(data_home, "Interlude")
        | path -> path

    Directory.CreateDirectory(game_dir) |> ignore
    Directory.SetCurrentDirectory(game_dir)

    let inline detect_temp_folder () : bool =
        executable_location.Replace("\\", "/").ToLower().Contains "appdata/local/temp" && OperatingSystem.IsWindows()

    let inline detect_missing_libraries () : bool =
        let exists name = File.Exists(Path.Combine(executable_location, name))

        let missing_bass =
            not(exists "bass.dll") && not(exists "libbass.so") && not(exists "libbass.dylib")

        let missing_bass_fx =
            not(exists "bass_fx.dll") && not(exists "libbass_fx.so") && not(exists "libbass_fx.dylib")

        missing_bass || missing_bass_fx

    let inline restart_on_exit () : unit =
        let executable = Path.Combine(executable_location, "Interlude.exe")
        let launch_dir = Path.GetDirectoryName(executable)

        let inline start_process () : unit =
            Process.Start(ProcessStartInfo(executable, WorkingDirectory = launch_dir, UseShellExecute = true)) |> ignore

        try
            start_process()
            printfn "Restarting"
        with err ->
            printfn "Automatic restart failed :("
            printfn "%O" err

    if detect_temp_folder() then
        crash_and_open_text_file
            "Hello ZIP FILE USER,\n\nplease EXTRACT all contents of the Interlude zip file before running the exe\notherwise it won't work or save any of your data.\n\nThanks,\nPercyqaz"

        -1
    elif detect_missing_libraries() then
        crash_and_open_text_file
            "Interlude is missing the audio dll/so/dylib files for your platform.\n\n If you are a developer, follow the build instructions at\n- https://github.com/YAVSRG/YAVSRG#readme -\n\n If you are not a developer, looks like you deleted a file you shouldn't have!\n Redownloading the game and extracting the zip over this folder (replace all) may fix it."

        -1
    else

    let mutex = new Mutex(true, "Interlude")
    let is_only_interlude_instance () = mutex.WaitOne(TimeSpan.Zero, true)

    if argv.Length > 0 then
        if is_only_interlude_instance() then
            printfn "Error: Interlude is not running!"
            mutex.ReleaseMutex()
        else
            send_ipc_message(String.concat " " argv)

    elif is_only_interlude_instance() then
        launch_new_instance(0)
        mutex.ReleaseMutex()

        if Updates.restart_on_exit && OperatingSystem.IsWindows() then
            mutex.Dispose()
            restart_on_exit()

    elif DEV_MODE then
        let instances = Process.GetProcessesByName("Interlude").Length
        launch_new_instance(instances - 1)
    else
        send_ipc_message("focus")

    Environment.Exit(0)
    0
