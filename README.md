# UnmanagedString


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




## 📢 Credits

* [dnlib](https://github.com/0xd4d/dnlib) — .NET Module/Assembly Reader/Writer Library
* [UnmanagedString (MrakDev)](https://github.com/MrakDev/UnmanagedString) — Original ASMResolver-based inspiration
* [UnmanagedString (TheHellTower)](https://github.com/TheHellTower/UnmanagedString) — Original dnlib-based inspiration