# Vendored ENet-CSharp native source

`enet.c` and `enet.h` are copied unmodified from
[nxrighthere/ENet-CSharp](https://github.com/nxrighthere/ENet-CSharp) tag **2.4.8**
(`Source/Native/`), MIT licensed (see `LICENSE`).

TABG ships this exact version (`enet_linked_version()` of its `libenet.so` reports 2.4.8), and
ENet-CSharp's wire format differs from upstream ENet, so we build the same C code instead of
using a generic ENet implementation. Do not update it without checking TABG's version.
