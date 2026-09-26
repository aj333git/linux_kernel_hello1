# F# as a Linux Kernel Module Development Orchestrator

**F# Linux Kernel Module Development Tool**

Build a Linux kernel module workflow with F#, .NET, Process, module signing, loading, unloading, and watch mode.

---

## Introduction

Linux kernel module development normally involves a repetitive sequence:

```text
make
sign
rmmod
insmod
dmesg
```

When experimenting with kernel modules, repeating these commands manually quickly becomes part of the development overhead.

This project uses **F# and .NET** to automate that workflow.

The F# script acts as a **user-space development orchestrator** for a kernel-space artifact. It starts Linux user-space programs such as `make`, `sudo`, `rmmod`, `insmod`, and `dmesg`, while the actual kernel module continues to execute inside the Linux kernel.

The complete development pipeline is:

```text
BUILD
↓
SIGN
↓
REMOVE OLD MODULE
↓
LOAD NEW MODULE
↓
SHOW KERNEL OUTPUT
```

The important architectural point is that **F# does not directly control the kernel module**. It orchestrates the Linux processes that interact with the kernel module.

---

## 1. The Development Workflow

Running:

```bash
dotnet fsi kloop.fsx loop
```

executes the complete development cycle:

```text
BUILD
↓
SIGN
↓
RMMOD
↓
INSMOD
↓
DMESG
```

The final state is intentional:

```text
hello.ko
↓
loaded into kernel
↓
hello module running
```

The old module is removed before the new module is loaded.

The loop therefore ends with the **new module loaded**, rather than leaving the kernel without the module.

---

## 2. Why F#?

F# provides a convenient way to turn a collection of Linux commands into a repeatable development workflow.

The script uses:

```fsharp
System.Diagnostics.Process
```

to start external Linux programs.

A simplified example is:

```fsharp
use proc = new Process()

proc.StartInfo <- psi
proc.Start() |> ignore

let output =
    proc.StandardOutput.ReadToEnd()

proc.WaitForExit()
```

This creates a clear boundary:

| Layer            | Responsibility                              |
| ---------------- | ------------------------------------------- |
| F# / .NET        | Orchestration                               |
| Linux user space | `make`, `sudo`, `insmod`, `rmmod`, `dmesg`  |
| Linux kernel     | Module loading, execution and logging       |
| Kernel module    | `hello_init()`, `hello_exit()`, `pr_info()` |

`System.Diagnostics.Process` is therefore being used as the **user-space process-control layer**.

---

## 3. F# Is Not the Kernel Module

The kernel module remains C code.

A minimal module looks like:

```c
static int __init hello_init(void)
{
    pr_info("hello: module2 loaded\n");
    return 0;
}

static void __exit hello_exit(void)
{
    pr_info("hello: module2 unloaded\n");
}

module_init(hello_init);
module_exit(hello_exit);
```

The F# script does not replace this kernel-space code.

Instead, it manages the development lifecycle around it:

```text
F# script
  |
  +-- make
  |
  +-- sign
  |
  +-- rmmod
  |
  +-- insmod
  |
  +-- dmesg
```

This separation is important.

**F# = development automation**

**C = kernel module**

**Linux kernel = execution environment**

---

## 4. BUILD — Compile the Kernel Module

The first stage is:

```fsharp
let build () =
    banner "1. BUILD KERNEL MODULE"

    let code =
        runAndPrint "make" ""

    if code <> 0 then
        failwith "BUILD FAILED"
```

The script launches:

```bash
make
```

The kernel module build system produces:

```text
hello.ko
```

The script also verifies that the resulting module actually exists:

```fsharp
if not (File.Exists(Path.Combine(workDir, moduleFile))) then
    failwith "hello.ko was not created"
```

This means the pipeline does not blindly continue after a failed build.

---

## 5. SIGN — Prepare the Kernel Module

The next stage signs the generated module.

The project uses the kernel's:

```text
scripts/sign-file
```

utility together with:

```text
MOK.key
MOK.crt
```

The signing command is conceptually:

```bash
sign-file sha256 MOK.key MOK.crt hello.ko
```

The F# implementation checks that the required signing infrastructure exists before attempting the operation.

For example:

```fsharp
if not (File.Exists(signKey)) then
    failwithf "Signing key not found: %s" signKey

if not (File.Exists(signCert)) then
    failwithf "Signing certificate not found: %s" signCert
```

The actual signing command is executed through:

```fsharp
runAndPrint "sudo" ...
```

The result is a signed:

```text
hello.ko
```

ready for the loading stage.

---

## 6. REMOVE — Unload the Existing Module

Before loading the new module, the script checks whether the old module is already present.

It executes:

```bash
lsmod
```

and searches for the module name.

The relevant logic is:

```fsharp
if loaded then
    runAndPrint
        "sudo"
        (sprintf "rmmod %s" moduleName)
```

This avoids blindly running:

```bash
rmmod hello
```

when the module is not loaded.

The purpose is simple:

```text
old hello module
↓
rmmod
↓
removed from kernel
```

This creates a clean state for loading the newly built module.

---

## 7. LOAD — Insert the New Module

The next stage loads the newly built and signed module:

```fsharp
let load () =
    banner "4. LOAD NEW MODULE INTO RAM"

    let code =
        runAndPrint
            "sudo"
            (sprintf "insmod %s" moduleFile)

    if code <> 0 then
        failwith "INSMOD FAILED"
```

Conceptually:

```bash
sudo insmod hello.ko
```

At this point the module is loaded into the Linux kernel.

The module's initialization function executes:

```c
hello_init()
```

which generates the kernel log message:

```text
hello: module2 loaded
```

---

## 8. SHOW — Read Kernel Output

The final stage observes the kernel log.

The project uses:

```bash
dmesg
```

and filters the output:

```bash
dmesg | grep 'hello:' | tail -n 10
```

The F# function is:

```fsharp
let show () =
    banner "5. KERNEL OUTPUT"

    runAndPrint
        "sudo"
        "bash -c \"dmesg | grep 'hello:' | tail -n 10\""
```

This gives the developer immediate feedback after the module has been loaded.

The development loop therefore becomes:

```text
source change
↓
build
↓
sign
↓
remove old module
↓
load new module
↓
inspect kernel output
```

---

## 9. The Complete Development Function

The entire workflow is deliberately simple:

```fsharp
let devLoop () =
    build ()
    sign ()
    unload ()
    load ()
    show ()
```

This is a synchronous orchestration pipeline.

Each operation must complete before the next operation begins.

The dependency chain is:

```text
BUILD
  |
  v
SIGN
  |
  v
RMMOD
  |
  v
INSMOD
  |
  v
DMESG
```

This ordering matters.

You cannot reliably load the new module before successfully producing the module artifact.

---

## 10. Command-Line Interface

The script exposes individual operations as well as the complete loop.

| Command  | Purpose                                                 |
| -------- | ------------------------------------------------------- |
| `build`  | Build `hello.ko`                                        |
| `sign`   | Sign `hello.ko`                                         |
| `unload` | Remove `hello` from the kernel                          |
| `load`   | Load `hello.ko`                                         |
| `show`   | Display kernel messages                                 |
| `loop`   | Execute the complete development pipeline               |
| `watch`  | Automatically execute the pipeline after source changes |

Examples:

```bash
dotnet fsi kloop.fsx build
```

```bash
dotnet fsi kloop.fsx sign
```

```bash
dotnet fsi kloop.fsx unload
```

```bash
dotnet fsi kloop.fsx load
```

```bash
dotnet fsi kloop.fsx show
```

The complete workflow is:

```bash
dotnet fsi kloop.fsx loop
```

---

## 11. WATCH MODE

The most useful part for iterative development is watch mode.

Run:

```bash
dotnet fsi kloop.fsx watch
```

The script monitors:

```text
hello.c
```

using:

```fsharp
File.GetLastWriteTimeUtc(sourceFile)
```

It periodically checks whether the timestamp has changed.

The core idea is:

```fsharp
if currentWrite <> lastWrite then
    lastWrite <- currentWrite
    devLoop ()
```

Therefore:

```text
edit hello.c
↓
save
↓
change detected
↓
BUILD
↓
SIGN
↓
RMMOD
↓
INSMOD
↓
DMESG
```

This turns the script into a lightweight kernel-module development loop.

---

## 12. Process Management with System.Diagnostics

The central .NET abstraction is:

```fsharp
System.Diagnostics.Process
```

The script constructs a `ProcessStartInfo`:

```fsharp
let psi = ProcessStartInfo()

psi.FileName <- command
psi.Arguments <- arguments
psi.WorkingDirectory <- workDir

psi.RedirectStandardOutput <- true
psi.RedirectStandardError <- true

psi.UseShellExecute <- false
```

Then the process is started:

```fsharp
use proc = new Process()

proc.StartInfo <- psi
proc.Start() |> ignore
```

Output and error streams are captured:

```fsharp
let output =
    proc.StandardOutput.ReadToEnd()

let error =
    proc.StandardError.ReadToEnd()
```

Finally:

```fsharp
proc.WaitForExit()
```

ensures the external command has finished.

The function returns:

```fsharp
proc.ExitCode, output, error
```

This gives the orchestration layer three important pieces of information:

1. Exit status
2. Standard output
3. Standard error

---

## 13. Why `use` Matters

The code uses:

```fsharp
use proc = new Process()
```

rather than simply creating a process object.

In F#, `use` provides automatic disposal for disposable resources.

Conceptually:

```text
create Process
↓
use Process
↓
run external program
↓
read output
↓
wait
↓
dispose
```

This is important because `Process` represents an operating-system resource.

The F# object itself lives in the managed environment, while the actual external process is managed by Linux.

---

## 14. The User-Space / Kernel-Space Boundary

The architecture can be understood as three layers.

### Layer 1 — F# / .NET

```text
dotnet
  |
  └── kloop.fsx
        |
        └── System.Diagnostics.Process
```

### Layer 2 — Linux User Space

```text
make
sudo
rmmod
insmod
dmesg
```

### Layer 3 — Linux Kernel

```text
Linux Kernel
  |
  ├── module loader
  ├── process management
  ├── kernel logging
  └── hello.ko
```

The important boundary is:

```text
F# / .NET
  |
  | process creation
  v
Linux user space
  |
  | kernel interfaces
  v
Linux kernel
```

`System.Diagnostics.Process` operates primarily on the **user-space side of this boundary**.

It does not provide kernel-module functionality itself.

---

## 15. Standard Output Is Also an OS-Level Channel

The script redirects output:

```fsharp
psi.RedirectStandardOutput <- true
```

and then reads:

```fsharp
proc.StandardOutput.ReadToEnd()
```

Conceptually:

```text
Linux process
  |
  | stdout
  v
pipe
  |
  v
F# Process object
  |
  v
StandardOutput
```

This is another example of F# interacting with the operating system through .NET abstractions.

The F# program is therefore not simply "running commands".

It is managing:

- process creation
- process lifetime
- exit codes
- standard output
- standard error
- filesystem state
- command sequencing

---

## 16. Why the New Module Remains Loaded

One important design decision is that the loop ends with:

```text
hello.ko
↓
loaded
↓
running
```

The development function does **not** call:

```bash
rmmod hello
```

after `show`.

That would defeat the purpose of the development loop.

Instead:

```fsharp
let devLoop () =
    build ()
    sign ()
    unload ()
    load ()
    show ()
```

leaves the newly loaded module running.

If the developer wants to remove it manually:

```bash
sudo rmmod hello
```

can be executed afterward.

---

## 17. A Small but Useful Development Tool

The project demonstrates an interesting combination:

| Technology   | Role                         |
| ------------ | ----------------------------- |
| C            | Linux kernel module          |
| Linux kernel | Module execution environment |
| Make         | Kernel module build          |
| `sign-file`  | Module signing               |
| `insmod`     | Module loading               |
| `rmmod`      | Module unloading             |
| `dmesg`      | Kernel log observation       |
| .NET         | User-space runtime           |
| F#           | Development orchestration    |

The important idea is not the size of the script.

The important idea is the **boundary between automation and the kernel**.

F# provides a high-level orchestration layer while the actual low-level kernel implementation remains C.

---

## 18. Architecture Summary

The complete architecture can be summarized as:

```text
F# / .NET
  |
  | System.Diagnostics.Process
  |
  +------> make
  |
  +------> sign-file
  |
  +------> rmmod
  |
  +------> insmod
  |
  +------> dmesg
  |
  v
Linux Kernel
  |
  v
hello.ko
  |
  v
hello_init()
  |
  v
pr_info()
```

The F# script is therefore best described as:

> **A user-space development orchestrator for a Linux kernel-space artifact.**

---

## 19. Complete Workflow

The resulting developer experience is:

```bash
# Manual complete workflow
dotnet fsi kloop.fsx loop
```

or:

```bash
# Automatic development workflow
dotnet fsi kloop.fsx watch
```

With watch mode enabled:

```text
hello.c
  |
  | edit
  v
change detected
  |
  v
make
  |
  v
hello.ko
  |
  v
sign
  |
  v
rmmod
  |
  v
insmod
  |
  v
dmesg
```

This removes repetitive command execution from the kernel-module development cycle while keeping the underlying Linux workflow explicit.

---

## Conclusion

This project is a practical example of using **F# as a systems-oriented automation language**.

The kernel module remains low-level C code.

Linux remains responsible for process management, module loading and kernel execution.

.NET provides the managed process-control abstraction.

F# ties everything together into a deterministic development pipeline:

```text
BUILD
→ SIGN
→ REMOVE
→ LOAD
→ OBSERVE
```

The result is a small but useful development tool that connects **F#, .NET process management and Linux kernel-module development** without hiding the underlying operating-system mechanisms.

---

## GitHub Repository

Source code, kernel module implementation and development tooling:

**[linux_kernel_hello1](https://github.com/aj333git/linux_kernel_hello1)**

Explore the complete project on GitHub:

https://github.com/aj333git/linux_kernel_hello1
