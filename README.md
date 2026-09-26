# F# as a Linux Kernel Module Development Orchestrator

**F# Linux Kernel Module Development Tool**

Automate a Linux kernel module workflow with F#, .NET's `Process` API, module signing, loading, unloading, and a file-watch development loop.

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

Repeating these commands by hand on every code change quickly becomes its own source of development overhead.

This project uses **F# and .NET** to automate that workflow.

The F# script acts as a **user-space development orchestrator** for a kernel-space artifact. It starts Linux user-space programs such as `make`, `sudo`, `rmmod`, `insmod`, and `dmesg`, while the kernel module itself continues to execute inside the Linux kernel.

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

The key architectural point is that **F# does not directly control the kernel module** — it orchestrates the Linux processes that interact with it.

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

The old module is removed before the new module is loaded, so the loop always ends with the **new module loaded**, rather than leaving the kernel without the module at all.

---

## 2. Why F#?

F# provides a convenient way to turn a collection of Linux commands into a repeatable development workflow.

The script uses:

```fsharp
System.Diagnostics.Process
```

to start external Linux programs. A simplified example:

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

`System.Diagnostics.Process` is therefore used as the **user-space process-control layer**, not as a way of touching the kernel itself.

---

## 3. F# Is Not the Kernel Module

The kernel module remains ordinary C code:

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

The F# script does not replace this kernel-space code — it manages the development lifecycle around it:

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

This separation is important:

**F# = development automation**

**C = kernel module**

**Linux kernel = execution environment**

---

## 4. BUILD — Compile the Kernel Module

```fsharp
let build () =
    banner "1. BUILD KERNEL MODULE"

    let code =
        runAndPrint "make" ""

    if code <> 0 then
        failwith "BUILD FAILED"
```

The script launches `make`, and the kernel module build system produces `hello.ko`. It also verifies the resulting artifact actually exists before continuing:

```fsharp
if not (File.Exists(Path.Combine(workDir, moduleFile))) then
    failwith "hello.ko was not created"
```

This means the pipeline never blindly continues after a failed build.

---

## 5. SIGN — Prepare the Kernel Module

The next stage signs the generated module using the kernel's own:

```text
scripts/sign-file
```

utility, together with a key and certificate pair:

```text
MOK.key
MOK.crt
```

Conceptually, the signing command is:

```bash
sign-file sha256 MOK.key MOK.crt hello.ko
```

The F# implementation checks that the required signing infrastructure exists before attempting the operation:

```fsharp
if not (File.Exists(signKey)) then
    failwithf "Signing key not found: %s" signKey

if not (File.Exists(signCert)) then
    failwithf "Signing certificate not found: %s" signCert
```

The actual signing command runs through:

```fsharp
runAndPrint "sudo" ...
```

The result is a signed `hello.ko`, ready for the loading stage.

---

## 6. REMOVE — Unload the Existing Module

Before loading the new module, the script checks whether the old module is already present by running:

```bash
lsmod
```

and searching for the module name:

```fsharp
if loaded then
    runAndPrint
        "sudo"
        (sprintf "rmmod %s" moduleName)
```

This avoids blindly running `rmmod hello` when the module isn't even loaded. The purpose is simple:

```text
old hello module
↓
rmmod
↓
removed from kernel
```

This leaves a clean state for loading the newly built module.

---

## 7. LOAD — Insert the New Module

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

Conceptually, this is:

```bash
sudo insmod hello.ko
```

At this point the module is loaded into the Linux kernel, and its initialization function executes:

```c
hello_init()
```

which produces the kernel log message:

```text
hello: module2 loaded
```

---

## 8. SHOW — Read Kernel Output

The final stage observes the kernel log using `dmesg`, filtered down to the module's own lines:

```bash
dmesg | grep 'hello:' | tail -n 10
```

```fsharp
let show () =
    banner "5. KERNEL OUTPUT"

    runAndPrint
        "sudo"
        "bash -c \"dmesg | grep 'hello:' | tail -n 10\""
```

This gives immediate feedback right after the module has been loaded, closing the loop:

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

```fsharp
let devLoop () =
    build ()
    sign ()
    unload ()
    load ()
    show ()
```

This is a synchronous orchestration pipeline — each operation must complete before the next begins:

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

This ordering matters: you cannot reliably load the new module before successfully producing the module artifact, and you cannot safely insert a new module while the old one still occupies its name.

---

## 10. Command-Line Interface

The script exposes each individual operation as well as the complete loop:

| Command  | Purpose                                                 |
| -------- | ------------------------------------------------------- |
| `build`  | Build `hello.ko`                                        |
| `sign`   | Sign `hello.ko`                                          |
| `unload` | Remove `hello` from the kernel                           |
| `load`   | Load `hello.ko`                                          |
| `show`   | Display kernel messages                                  |
| `loop`   | Execute the complete development pipeline                |
| `watch`  | Automatically execute the pipeline after source changes  |

Examples:

```bash
dotnet fsi kloop.fsx build
dotnet fsi kloop.fsx sign
dotnet fsi kloop.fsx unload
dotnet fsi kloop.fsx load
dotnet fsi kloop.fsx show
```

The complete workflow:

```bash
dotnet fsi kloop.fsx loop
```

---

## 11. WATCH MODE

The most useful part for iterative development is watch mode:

```bash
dotnet fsi kloop.fsx watch
```

The script monitors `hello.c` using:

```fsharp
File.GetLastWriteTimeUtc(sourceFile)
```

periodically checking whether the timestamp has changed:

```fsharp
if currentWrite <> lastWrite then
    lastWrite <- currentWrite
    devLoop ()
```

So the loop becomes:

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

This turns the script into a lightweight kernel-module development loop — save the file, and everything downstream happens automatically.

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

then starts the process:

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

and finally:

```fsharp
proc.WaitForExit()
```

ensures the external command has actually finished before the function returns:

```fsharp
proc.ExitCode, output, error
```

This gives the orchestration layer three essential pieces of information: exit status, standard output, and standard error.

---

## 13. Why `use` Matters

```fsharp
use proc = new Process()
```

In F#, `use` provides automatic disposal for disposable resources:

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

This matters because `Process` represents a real operating-system resource. The F# object itself lives in the managed environment, while the actual external process is managed by Linux — `use` guarantees that resource is released deterministically, without relying on the garbage collector or manual cleanup.

---

## 14. The User-Space / Kernel-Space Boundary

The architecture is best understood as three layers.

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

`System.Diagnostics.Process` operates on the **user-space side of this boundary** — it does not provide kernel-module functionality itself.

---

## 15. Standard Output Is Also an OS-Level Channel

```fsharp
psi.RedirectStandardOutput <- true
```

and later:

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

The F# program is therefore not simply "running commands" — it is managing process creation, process lifetime, exit codes, standard output, standard error, filesystem state, and command sequencing, all through .NET's abstraction over the operating system.

---

## 16. Why the New Module Remains Loaded

One deliberate design decision is that the loop ends with:

```text
hello.ko
↓
loaded
↓
running
```

The development function does **not** call `rmmod hello` after `show` — that would defeat the entire purpose of the loop:

```fsharp
let devLoop () =
    build ()
    sign ()
    unload ()
    load ()
    show ()
```

The newly loaded module stays running. If it needs removing manually, that's a deliberate separate step:

```bash
sudo rmmod hello
```

---

## 17. A Small but Useful Development Tool

| Technology   | Role                          |
| ------------ | ------------------------------ |
| C            | Linux kernel module            |
| Linux kernel | Module execution environment   |
| Make         | Kernel module build            |
| `sign-file`  | Module signing                 |
| `insmod`     | Module loading                 |
| `rmmod`      | Module unloading               |
| `dmesg`      | Kernel log observation         |
| .NET         | User-space runtime             |
| F#           | Development orchestration      |

The interesting idea here isn't the size of the script — it's the **boundary between automation and the kernel**. F# provides a high-level orchestration layer while the low-level kernel implementation stays exactly where it belongs: in C.

---

## 18. Architecture Summary

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

The F# script is best described as:

> **A user-space development orchestrator for a Linux kernel-space artifact.**

---

## 19. Complete Workflow

```bash
# Manual complete workflow
dotnet fsi kloop.fsx loop
```

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

This removes repetitive command execution from the kernel-module development cycle while keeping the underlying Linux workflow fully explicit and inspectable.

---

## Conclusion

This project is a practical example of using **F# as a systems-oriented automation language**.

The kernel module remains low-level C code. Linux remains responsible for process management, module loading, and kernel execution. .NET provides the managed process-control abstraction. F# ties everything together into a deterministic development pipeline:

```text
BUILD
→ SIGN
→ REMOVE
→ LOAD
→ OBSERVE
```

The result is a small but genuinely useful development tool connecting **F#, .NET process management, and Linux kernel-module development** — without hiding any of the underlying operating-system mechanisms.
