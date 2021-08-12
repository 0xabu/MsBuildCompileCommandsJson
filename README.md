# MSBuild `compile_commands.json` logger

This is a simple
[MSBuild logger](https://docs.microsoft.com/en-us/visualstudio/msbuild/build-loggers)
that emits a
[Clang-style `compile_commands.json` file](https://clang.llvm.org/docs/JSONCompilationDatabase.html)
by observing the MSVC compiler invocations when a C++ project is built. It is particularly useful
with [Visual Studio Code's C/C++ extension](https://code.visualstudio.com/docs/cpp/), which
[can be configured](https://code.visualstudio.com/docs/cpp/c-cpp-properties-schema-reference#_configuration-properties)
to use `compile_commands.json` to determine the compiler options (include paths,
defines, etc.) for accurate IntelliSense.

## Usage

Building the project is straightforward:
```
dotnet build
```

Then, invoke MSBuild with [the `-logger` option](https://docs.microsoft.com/en-us/visualstudio/msbuild/msbuild-command-line-reference).
For example:
```
msbuild -logger:/path/to/CompileCommandsJson.dll MyProject
```

By default, `compile_commands.json` is written in the current directory. You can
control the output path using a parameter, e.g.:
```
msbuild -logger:/path/to/CompileCommandsJson.dll;my_new_compile_commands.json MyProject
```

## Limitations

There are two significant design limitations:

 1. The logger will only emit entries for compiler invocations that it observes
    during a build; in particular, for an incremental build, there will be no
    output for any targets that are considered up to date.

 2. The logger truncates the JSON file at startup, and writes to it
    incrementally throughout the build.

Thus, for an accurate result you should use this logger only on a completely
clean build, and to avoid confusing tools (such as VSCode) that may observe the
file as it is written, you should probably write the output to a temporary file
and rename it only after the build succeeds. Typical usage is roughly:

```
rm -r out
msbuild -logger:CompileCommandsLogger.dll;cctmp.json
mv cctmp.json compile_commands.json
```

 ## Author

 Andrew Baumann