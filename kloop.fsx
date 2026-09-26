open System
open System.Diagnostics
open System.IO

// ============================================================
// CONFIGURATION
// ============================================================

let workDir = __SOURCE_DIRECTORY__

let moduleName = "hello"
let moduleFile = "hello.ko"

// Kernel module signing tools/keys
let signFile =
    sprintf
        "/usr/src/linux-headers-%s/scripts/sign-file"
        (Environment.GetEnvironmentVariable("KERNELRELEASE")
         |> fun x ->
             if String.IsNullOrWhiteSpace(x) then
                 // Fallback: ask uname
                 let psi = ProcessStartInfo()
                 psi.FileName <- "uname"
                 psi.Arguments <- "-r"
                 psi.RedirectStandardOutput <- true
                 psi.UseShellExecute <- false

                 use p = new Process()
                 p.StartInfo <- psi
                 p.Start() |> ignore

                 let result = p.StandardOutput.ReadToEnd().Trim()
                 p.WaitForExit()
                 result
             else
                 x)

let signKey =
    Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "kernel_keys",
        "MOK.key"
    )

let signCert =
    Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "kernel_keys",
        "MOK.crt"
    )


// ============================================================
// RUN A LINUX COMMAND
// ============================================================

let run command arguments =
    let psi = ProcessStartInfo()

    psi.FileName <- command
    psi.Arguments <- arguments
    psi.WorkingDirectory <- workDir

    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true

    psi.UseShellExecute <- false

    use proc = new Process()

    proc.StartInfo <- psi
    proc.Start() |> ignore

    let output =
        proc.StandardOutput.ReadToEnd()

    let error =
        proc.StandardError.ReadToEnd()

    proc.WaitForExit()

    proc.ExitCode, output, error


// ============================================================
// RUN COMMAND AND PRINT OUTPUT
// ============================================================

let runAndPrint command arguments =
    let code, output, error =
        run command arguments

    if output <> "" then
        printf "%s" output

    if error <> "" then
        printf "%s" error

    code


// ============================================================
// BANNER
// ============================================================

let banner text =
    printfn ""
    printfn "============================================================"
    printfn "%s" text
    printfn "============================================================"


// ============================================================
// 1. BUILD
// ============================================================

let build () =
    banner "1. BUILD KERNEL MODULE"

    let code =
        runAndPrint "make" ""

    if code <> 0 then
        failwith "BUILD FAILED"

    if not (File.Exists(Path.Combine(workDir, moduleFile))) then
        failwith "hello.ko was not created"

    printfn "Build successful: %s" moduleFile


// ============================================================
// 2. SIGN
// ============================================================

let sign () =
    banner "2. SIGN KERNEL MODULE"

    let fullSignFile =
        signFile

    if not (File.Exists(fullSignFile)) then
        failwithf
            "sign-file not found: %s"
            fullSignFile

    if not (File.Exists(signKey)) then
        failwithf
            "Signing key not found: %s"
            signKey

    if not (File.Exists(signCert)) then
        failwithf
            "Signing certificate not found: %s"
            signCert

    let arguments =
        sprintf
            "sha256 \"%s\" \"%s\" \"%s\""
            signKey
            signCert
            moduleFile

    let code =
        runAndPrint "sudo" (
            sprintf
                "\"%s\" %s"
                fullSignFile
                arguments
        )

    if code <> 0 then
        failwith "MODULE SIGNING FAILED"

    printfn "Module signing successful."


// ============================================================
// 3. REMOVE OLD MODULE FROM RAM
// ============================================================

let unload () =
    banner "3. REMOVE OLD MODULE FROM RAM"

    // Check whether hello is currently loaded.
    let code, output, _ =
        run "lsmod" ""

    let loaded =
        code = 0
        && (
            output
            |> fun text ->
                text.Split(
                    [| '\n'; '\r' |],
                    StringSplitOptions.RemoveEmptyEntries
                )
                |> Array.exists (
                    fun line ->
                        line.StartsWith(moduleName + " ")
                        || line = moduleName
                )
        )

    if loaded then
        printfn "Module %s is currently loaded." moduleName
        printfn "Removing old module..."

        let removeCode =
            runAndPrint
                "sudo"
                (sprintf "rmmod %s" moduleName)

        if removeCode <> 0 then
            failwith "RMMOD FAILED"

        printfn "Old module removed from RAM."
    else
        printfn "Module %s is not currently loaded." moduleName
        printfn "Nothing to remove."


// ============================================================
// 4. LOAD NEW MODULE INTO RAM
// ============================================================

let load () =
    banner "4. LOAD NEW MODULE INTO RAM"

    printfn "Loading signed %s..." moduleFile

    let code =
        runAndPrint
            "sudo"
            (sprintf "insmod %s" moduleFile)

    if code <> 0 then
        failwith "INSMOD FAILED"

    printfn "New module successfully loaded into RAM."


// ============================================================
// 5. SHOW KERNEL OUTPUT
// ============================================================

let show () =
    banner "5. KERNEL OUTPUT"

    let code =
        runAndPrint
            "sudo"
            "bash -c \"dmesg | grep 'hello:' | tail -n 10\""

    if code <> 0 then
        printfn "Could not read kernel log."



// ============================================================
// COMPLETE DEVELOPMENT LOOP
// ============================================================

let devLoop () =
    build ()
    sign ()
    unload ()
    load ()
    show ()


// ============================================================
// WATCH MODE
// ============================================================

let watch () =
    let sourceFile =
        Path.Combine(workDir, "hello.c")

    if not (File.Exists(sourceFile)) then
        failwith "hello.c not found"

    let mutable lastWrite =
        File.GetLastWriteTimeUtc(sourceFile)

    banner "WATCH MODE"

    printfn "Watching:"
    printfn "  %s" sourceFile
    printfn ""
    printfn "Save hello.c to rebuild/reload the module."
    printfn "Press Ctrl+C to stop."

    while true do

        System.Threading.Thread.Sleep(500)

        let currentWrite =
            File.GetLastWriteTimeUtc(sourceFile)

        if currentWrite <> lastWrite then

            lastWrite <- currentWrite

            printfn ""
            printfn "[CHANGE DETECTED]"

            try
                devLoop ()
            with
            | ex ->
                printfn ""
                printfn "ERROR: %s" ex.Message


// ============================================================
// HELP
// ============================================================

let usage () =
    printfn """
Linux Kernel Module Development Tool

Usage:

  dotnet fsi kloop.fsx build
      Build hello.ko

  dotnet fsi kloop.fsx sign
      Sign hello.ko

  dotnet fsi kloop.fsx unload
      Remove hello from RAM

  dotnet fsi kloop.fsx load
      Load hello.ko into RAM

  dotnet fsi kloop.fsx show
      Show hello kernel messages

  dotnet fsi kloop.fsx loop
      BUILD -> SIGN -> REMOVE -> LOAD -> SHOW

  dotnet fsi kloop.fsx watch
      Automatically run the development loop
      whenever hello.c changes
"""


// ============================================================
// COMMAND DISPATCH
// ============================================================

match fsi.CommandLineArgs |> Array.skip 1 with

| [| "build" |] ->
    build ()

| [| "sign" |] ->
    sign ()

| [| "unload" |] ->
    unload ()

| [| "load" |] ->
    load ()

| [| "show" |] ->
    show ()

| [| "loop" |] ->
    devLoop ()

| [| "watch" |] ->
    watch ()

| _ ->
    usage ()

