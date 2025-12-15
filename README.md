# UnmanagedString

[![CodeFactor](https://www.codefactor.io/repository/github/thehelltower/UnmanagedString/badge)](https://www.codefactor.io/repository/github/thehelltower/UnmanagedString)

## 📜 What is UnmanagedString ?

**UnmanagedString** is a dnlib-based .NET post-processing tool that relocates managed string literals into **native (unmanaged) code** and reconstructs them at runtime using unsafe string constructors.

Instead of embedding strings as regular `ldstr` metadata entries, the tool:

1. Replaces `ldstr` instructions with calls to injected **native methods**
2. Stores the raw string bytes directly in the PE file as native machine code
3. Returns a pointer (`sbyte*` or `char*`) to those bytes
4. Reconstructs the managed `string` at runtime using pointer-based constructors

This removes the original string data from the managed metadata stream entirely.

The project is implemented **purely with dnlib**, without relying on ConfuserEx internals or ASMResolver.

---

## ⚠️ Disclaimer

You are responsible for how you use this tool.

Do **not** use it to:
- Hide malicious behavior
- Bypass software licensing or DRM
- Evade security products in real-world deployments

Always comply with local laws and software licenses.

---

## 🎯 Key Features

- ✅ **Pure dnlib implementation**  
  No ConfuserEx services, no ASMResolver dependency

- ✅ **Native string storage**  
  Strings are embedded directly in native code, not metadata

- ✅ **x86 and x64 support**  
  Generates position-independent native stubs for both architectures

- ✅ **Automatic encoding selection**  
  Uses ASCII when possible, Unicode when required

- ✅ **Correct RVA patching**  
  Native method entry points are written via `ModuleWriterEvent`

- ✅ **String deduplication**  
  Identical strings are stored only once

- ✅ **Safe reconstruction**  
  Uses appropriate `string(char*)` or `string(sbyte*, int, int)` constructors

---

## 🔍 Example

**Original:**
```csharp
Console.WriteLine("Hello, world!");
````

**Protected:**

```csharp
Console.WriteLine(new string(<Module>.00ffc87a1ad544b6a1f00671960148e5()));
```

Behind the scenes, the native method returns a pointer to raw bytes embedded in the PE file, and the managed string is reconstructed at runtime.

---

## 🧪 Testing checklist (before trusting output)

* Run protected binaries on **x86** and **x64** systems
* Verify UI apps load resources (icons, images, localized strings)
* Attach a debugger and ensure native method RVAs resolve correctly
* Test reflection-heavy code (type names, method names, attributes)
* Validate behavior on large, real-world assemblies
* Confirm no crashes on startup (`TypeLoadException`, bad entry points)

---

## 📢 Credits

* [dnlib](https://github.com/0xd4d/dnlib) — .NET Module/Assembly Reader/Writer Library
* [UnmanagedString (MrakDev)](https://github.com/MrakDev/UnmanagedString) — Original ASMResolver-based inspiration