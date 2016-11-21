# MONO EMSCRIPTEN PROTOTYPE NOTES

This is a small repo for testing an attempt at an emscripten port of mono. Enclosed are a C# test program, a C "driver" program, and these instructions. The current prototype gets partially into runtime startup before failing when it tries to load assemblies.

If you just want to run the prototype, skip to "How to run this" at the end.

## Why I did this

Emscripten is a C-to-Javascript compiler produced by Mozilla. Its compiler (`emcc`) compiles C to LLVM bitcode (its ".o" and ".a" files are literally this). Its linker links LLVM bitcode files into asmjs, which is a restricted subset of JavaScript. Its libc fakes many things which you normally expect in C but which are not part of a web browser (for example, a filesystem).

The team responsible for Emscripten is deeply involved with the effort for WebAssembly, which is a bytecode format that can be executed by web browsers. WebAssembly is very close to an initial public release. There is a branch of Emscripten which can output WebAssembly now. There is an experimental version of Firefox you can download which executes WebAssembly, and it will probably be in "real" Firefox by spring 2017.

I think supporting WebAssembly is an important long term goal for Mono. Emscripten is by far the most plausible means to do this.

## Prototype scope

My plan was to compile libmono using Emscripten, and link it with bitcode from Mono's AOT bitcode compiler. My goals with this project were

- See how *close* I can get to something working
- Start to catalog the distance between "this is what we have" and "this is a shippable product"
- Identify whether there are specific feaure asks we can make of the Emscripten/WebAssembly teams that would allow us to close the gap to "something worth shipping".

## Platform limitations

There are some things Emscripten cannot do but which we usually depend on.

1. No signals
2. No register access, no access to ucontext_t or equivalent, no stack access, no unwinding. (I believe it's actually storing "registers" in JavaScript variables and using the JavaScript stack as the stack.)
3. No *threads*. (Javascript has a model for spawning simultaneous execution threads, but they are not memory-sharing.)
4. Not all LLVM bitcode ops/intrinsics are supported. The stated goal of the emscripten project is to support the bitcode emitted by Clang. They view support for LLVM bitcode emitted by other sources (like us) as a "nice-to-have".

Some of these limitations are similar to those we already encounter on platforms like the Apple Watch. The llvm bitcode and cooperative GC work we have already done get us a long way toward something that works.

Some of the remaining limitations will improve in future. WebAssembly is more isolated from JavaScript than asmjs and might support something more like a "real" stack; I haven't investigated. There is something called SharedArrayBuffer which may be added to JavaScript soon, and I hear reports that c++11 threads are a candidate feature for wasm for post-1.0.

The only real blockers here I think are the lack of any stack-walking primitives, which will hurt our GC badly, and the lack of threads. We can deal with the former by rewriting our GC, if we decide it is worth it. The latter is a more serious problem, especially if we want to create something fundamentally more useful than existing projects such as Katelyn Gadd's JSIL.

## What happened

### Trivial test

My first pass was to write a small C# program containing no memory usage and no corlib usage. The Main looked like:

    int x = 5; int y = 1;
    while (x > 0) {
        y *= x; x -= 1;
    }
    return y;

It was trivially easy to compile this into bitcode and get emscripten to emit a js file; I could embed the js file in a browser; I could call _Test_Program_Main_string__() in the JavaScript console and it returned `120`. Success.

### Minimal test

My next pass was to add a single object allocation to the test program. This did not immediately work because there was no libmono and therefore no GOT.

To make this test work, I had to build an Emscripten-compiled libmono; I also needed to AOT compile mscorlib and Emscripten-compile a small driver program that uses the embedding API to invoke libmono. My model for this was the launcher program from our PS4 port.

I spent about two weeks on this first-pass prototype. Here is where I wound up with:

I can sucessfully compile all of libmono; whatever combination of C# standard libraries I want; the C# test program; the C driver program; and a packed filesystem including mscorlib.dll, together into a js file. The js file is 192 MB in size. I can run the js file in Node; it takes twenty seconds before anything starts happening, I assume because the js file is 192 MB in size. (I did the sketchiest possible browser test and it did not seem to have the load-time issue.) When Node runs the js file, libmono loads and the runtime gets a significant portion of the way into initializing. It then fails with `Runtime critical type System.Object not found`: it does successfully load the mscorlib.dll assembly itself, but is unable to find System.Object's data in the aot cache, and thus fails to load the class.

I believe this is all very promising and indicates we likely need to only solve a few problems before the minimal test can run. However, we would probably need to solve the js file size / load time problem to create anything remotely useful to a customer.

## What exactly I did

Here are the changes I made to libmono to make it compile and run:

- Applied a patch from Zoltan which causes us to use `LLVMInt32Type ()` in the place we currently use `LLVMInt8Type ()` in mini-llvm.c (this is necessary because emscripten does not support the `llvm.expect.i8` intrinsic, only `llvm.expect.i32`).
- Added support for a `asmjs-local-emscripten` triple to configure.ac, mono-config.c
- Changed `mono_thread_info_attach` to assert if called twice; TLS is currently broken (see "remaining issues" below), so unless I assert early it fails later in a confusing place
- Added `#ifndef HOST_EMSCRIPTEN` to effectively comment out:
    - Most of `register_thread` in mono-threads.c, skipping many important parts of runtime init
    - The bodies of `sgen_unified_suspend_stop_world()`, `mono_memory_barrier()`, `mono_memory_read_barrier()` and `mono_memory_write_barrier()` (these return without doing anything)
    - The bodies of `async_suspend_critical()` and `async_abort_critical()` in threads.c (these return `KeepSuspended`)
    - The bodies of nearly every function in mini-exceptions.c (these call `g_assert_not_reached` when called)
    - The bodies of all our semaphore functions (init, destroy and post are noops; wait and timedwait are `g_error`s)
    - The body of `is_thread_in_critical_region()` in mono-threads.c (returns FALSE)
    - The entirety of mono-context.h
    - An assert `g_assert (*code_end > *code_start);` in `compute_llvm_code_range ()` in aot-runtime.c which was failing (Rodrigo believes that if this assert is not met, many things will break)
    - An assert `g_assert (sb_header == sb_header_for_addr (sb_header, desc->block_size));` in `alloc_sb` in lock-free-alloc.c which was failing. (This seems… bad.)
    - One call to `mono_thread_info_attach()` in domain.c (see above)
    - Mono.Posix.Helper signals and "remap" support

    Obviously, these `#ifndef`s were incredibly scattershot, and should be expected to break a number of important things.
- Fixed the `madvise` calls in `mono_mprotect()` to use `posix_madvise` in Emscripten to work around a missing symbol issue.
- Added mini-emscripten.h, mini-emscripten.c (these activate on `HOST_EMSCRIPTEN`), and mono-threads-emscripten.c (this last one activates with `USE_EMSCRIPTEN_BACKEND`, which is set by `HOST_EMSCRIPTEN`). They're mostly full of stub methods. I define the counts of all register types to be 0.
- Fixed what appears to be an actual typo in atomic.c??

## Remaining issues

### Major TODO items

- **Cannot load System.Object issue:** This could be as simple as occurring because I compiled mscorlib wrong, or because the aot cache depends on something which is broken. Rodrigo Kumpera suggests however that our current AOT format might be fundamentally incompatible with asmjs, because it assumes it can do certain kinds of math on function pointers that asmjs is unlikely to support.
- **TLS is completely broken:** it *appears* TLS reads are just always returning 0. I suspect this is because of either the code I commented out in `register thread`, or because of whatever problem was causing the assert in lock-free-alloc.c to fail.
- **SGen:** Without threads, our system for stopping the world during a collection will have to be revised slightly. Without stack walking (and `llvm_eh_unwind_init`?) our system for determining roots may have to be revised majorly.
- **The file size / load time problem:** Hopefully this will solve itself with a combination of running the C# linker, targeting WebAssembly (this is both smaller and quicker to parse), and running in a browser (browsers are optimized to load large JavaScript files; Node.js is not).
- **Most of the stuff I commented out to make libmono compile** (exceptions, the scary assert in `compute_llvm_code_range()`?) obviously needs to be restored
- **Threads:** Hopefully WebAssembly will solve this problem for us.
- **Trampolines:** I did not even attempt to make trampolines work.

I also think it is worth sometime soon approaching WebAssembly to ask about, at minimum:

- Getting some sort of primitive to make stack walking or something like it possible (surely we will not be the only GC'd language to need this).
- Adding a couple of the trivial missing llvm opcodes (see below)
- Trying to convince them to add support for dynamic libraries (asmjs has this, WebAssembly does not; our file size issues may get less bad if each browser only has to download libmono/mscorlib once).
- What WebAssembly's plans are with threads.

### Minor things that don't work

- Besides `llvm.expect.i8`, building our bitcode files right now produces the following warnings:
        LLVM failed for 'Write': opcode oparglist
        LLVM failed for 'WriteLine': opcode oparglist
        LLVM failed for 'CreateIUnknown': non-default callconv
        LLVM failed for 'Concat': opcode oparglist
        LLVM failed for 'CoCreateInstance': non-default callconv
    Obviously not being able to run `Console.WriteLine` is a little embarrassing.
- *Linking* the prototype results in a series of warnings, some worrisome:
    - Many `mono_` functions are still missing
    - All these functions, some apparently LLVM intrinsics, are missing: `llvm_sin_f64`, `llvm_returnaddress`, `pthread_getschedparam`, `inotify_rm_watch`, `inotify_add_watch`, `llvm_x86_sse2_pause`, `getgrnam`, `pthread_attr_getstacksize`, `inotify_init`, `llvm_nacl_atomic_cmpxchg_i64`, `pthread_setschedparam`, `getgrgid`, `pthread_attr_setschedparam`, `wapi_GetVolumeInformation`, `llvm_eh_unwind_init`, `llvm_cos_f64`, `pthread_attr_getschedpolicy`, `gc_stats`
    - Many, many warnings about functions having an "unexpected number of arguments"; I assume this is some sort of calling convention thing:
        warning: unexpected number of arguments 4 in call to 'mscorlib_System_Array_qsort_System_Decimal_System_Decimal___int_int', should be 3

    Note that, somewhat terrifyingly, link errors in emscripten are *warnings*; you get a warning at link time, and then an exception thrown at runtime if you try to call a nonexistent function.

- In my test steps (see below) I don't use `--with-runtime_preset=mobile_static`. I probably should.

### Entirely abstract issues

These do not actually need to be solved, but are sort of general Mono design issues that made this experiment more awkward than it could be. These are things to think about if in future we want Mono to elegantly support "things like WebAssembly":

- Right now the mono code generally assumes that every single platform we run on, we have EITHER a jit compiler OR an aot compiler. This is inherently not the case for "bitcode-only" platforms, where we have a runtime but no compiler (we outsource that to llvm) but random chunks of the aot compiler get compiled into the runtime anyway. This leads to things like having to define the number of registers for asmjs-local-emscripten even though this is not meaningful.
- There's some kind of weird thing where you can't aot-compile one mscorlib if the aot compiler has loaded a different mscorlib. There's workarounds for this but they're awkward.
- It would be neat, when compiling with `--aot=static`, if we could embed some assemblies directly into the executable. The UWP team was also asking about this.

## How to run this

My repro steps are ad hoc and are mostly shaped by a desire to not ever have to run `make install`. But:

Install emscripten and node.js (I suggest doing this via Homebrew). Check out this repository and cd to it. In a terminal run the following steps:

    # Check out my emscripten prototype branch; we'll build the aot compiler in this directory.
    git clone --single-branch -b ems_test git@github.com:xmcclure/mono.git mono_compile
    (cd mono_compile && git reset --hard c8b6aa0503a57ce01c750695728b28b6a6f3d4df)

    # Check out a second copy to build the runtime in.
    git clone --single-branch -b ems_test git@github.com:xmcclure/mono.git mono_runtime
    (cd mono_runtime && git reset --hard c8b6aa0503a57ce01c750695728b28b6a6f3d4df)

    # Check out mono's llvm fork. We'll need this to do anything.
    git clone --single-branch -b mono-2016-02-13-2acdc6d60c5199a3b8957d851b62691d71756d08 https://github.com/mono/llvm mono_llvm_src

    # Build llvm and install it into a "mono_llvm" dir
    cd mono_llvm_src
    (cd cmake && cmake -G "Unix Makefiles" -DCMAKE_INSTALL_PREFIX=`pwd`/../../mono_llvm ..)
    (cd cmake && make install)
    cd ..

    # Build compiler. Also build a mobile_static corlib and disable ALWAYS_AOT.
    cd mono_compile
    ./autogen.sh --enable-nls=no "CFLAGS=-O0" CC="ccache clang" --disable-boehm --with-sigaltstack=no --prefix=`pwd`/../mono_compile_install --enable-maintainer-mode --enable-llvm --enable-llvm-runtime --with-llvm=`pwd`/../mono_llvm
    make -j8
    (cd mcs/class/corlib && make PROFILE=mobile_static ALWAYS_AOT=)
    cd ..

    # Build runtime. You're going to be doing this with emscripten, so it looks a little funny.
    # Running "make" will get only as far as starting to build the standard library, then fail
    # with an error about mcs or jay/jay. That's fine, keep going, we only need the static libs.
    cd mono_runtime
    emconfigure ./autogen.sh --enable-nls=no --disable-boehm --with-sigaltstack=no --prefix=`pwd`/../mono_runtime_install --enable-maintainer-mode --with-cooperative-gc=yes --enable-division-check --with-sgen-default-concurrent=no --host=asmjs-local-emscripten --enable-minimal=jit
    emmake make
    cd ..

    # Now we're going to actually build the emscripten example. First let's open a new bash session:
    bash

    # And set some environment variables.
    # This is mostly to set us up to use the AOT compiler without actually installing it.
    export TESTS=`pwd`;
    export COMPILER=$TESTS/mono_compile RUNTIME=$TESTS/mono_runtime;
    export COMPILER_BIN=$COMPILER/runtime/_tmpinst/bin;
    export MONO_CFG_DIR=$COMPILER/runtime/etc MONO_PATH=$COMPILER/mcs/class/lib/net_4_x;
    export PATH=$PATH:"$TESTS/mono_llvm/bin";
    export MSCORLIB_PATH=$COMPILER/mcs/class/lib/mobile_static;
    export MSCORLIB=$MSCORLIB_PATH/mscorlib.dll

    # This will become the virtual filesystem of the emscripten program
    mkdir -p assembly

    # Build mscorlib into bytecode in the current directory.
    MONO_PATH=$MSCORLIB_PATH MONO_ENABLE_COOP=1 $COMPILER/mono/mini/mono --aot=static,llvmonly,asmonly,llvm-outfile=mscorlib.bc $MSCORLIB

    # Copy the assembly into the virtual filesystem and strip its IL.
    cp $MSCORLIB assembly
    $COMPILER/mono/mini/mono $COMPILER/mcs/class/lib/net_4_x/mono-cil-strip.exe assembly/mscorlib.dll

    # Compile the test program.
    $COMPILER_BIN/mcs program.cs -t:library -out:program.dll -debug:full

    # AOT the test program, again to bitcode in this directory.
    MONO_ENABLE_COOP=1 $COMPILER/mono/mini/mono --aot=static,llvmonly,asmonly,llvm-outfile=program.bc program.dll

    # Copy the test program's assembly into the virtual file system and strip that IL too.
    cp program.dll assembly/program.dll
    $COMPILER/mono/mini/mono $COMPILER/mcs/class/lib/net_4_x/mono-cil-strip.exe assembly/program.dll

    # Build the C driver.
    emcc -c driver.c

    # Link.
    # A few things to note here: The mono libraries have to all get defined as a group,
    # since they have recursive dependencies; the emscripten linker takes extra arguments
    # via -s, which we use to set the heap, set the virutal file system, and make sure
    # void main() is visible to node.
    emcc -L$RUNTIME/mono/sgen/.libs -L$RUNTIME/mono/mini/.libs -L$RUNTIME/eglib/src/.libs -L$RUNTIME/mono/metadata/.libs -L$RUNTIME/mono/io-layer/.libs program.bc -L$RUNTIME/mono/utils/.libs mscorlib.bc driver.o -Wl,--start-group -lmonoutils -lmini-static -lmonoruntimesgen-static -lmonosgen-static -lwapi -Wl,--end-group -leglib -o csharp.js -s EXPORTED_FUNCTIONS='["_main"]' --embed-file assembly\@/ -s TOTAL_MEMORY=134217728

    # Run a small node program that loads the emscripten output and executes it.
    # If this worked, it would print "120".
    node test.js