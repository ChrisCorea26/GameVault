# Native components

## rcheevos (RetroAchievements)
`rcheevos/` is an unmodified copy of [rcheevos](https://github.com/RetroAchievements/rcheevos)
**v12.5.0** (MIT licence, `rcheevos/LICENSE`). It contains only `include/`, `src/` and the licence;
the upstream Visual Studio project and Makefiles were left out.

`CMakeLists.txt` builds the parts GameVault uses (rc_client, the runtime, the API and MD5) as a shared
library. `src/GameVault.Platform` builds it automatically with CMake when `out/<rid>/` has no library
for the current machine. That needs CMake and a C compiler:
- **Windows:** Visual Studio with the "Desktop development with C++" workload.
- **Linux:** gcc or clang.

To build it by hand:

```
cmake -S native -B native/build/win-x64 -DGV_OUT=native/out/win-x64
cmake --build native/build/win-x64 --config Release
```

**Updating:**
1. Replace `rcheevos/include` and `rcheevos/src` with the new release.
2. Check `CMakeLists.txt`'s file list against upstream's `src/Makefile.common`.
3. Check the struct layouts in `src/GameVault.Platform/Achievements/RcheevosNative.cs` against
   `include/rc_client.h`.
4. Delete `native/out` and run the tests.
