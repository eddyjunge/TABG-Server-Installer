//! Embed the map dumper plugin (Tabg.WorldDumper.dll, our own MIT code) so `tabg setup` works from
//! a single binary. Taken from `TABG_EMBED_DUMPER`, or the repo's Release build output if present.

use std::path::PathBuf;

fn main() {
    println!("cargo:rerun-if-env-changed=TABG_EMBED_DUMPER");
    let manifest = PathBuf::from(std::env::var("CARGO_MANIFEST_DIR").unwrap());
    let default =
        manifest.join("../../mods/src/WorldDumper/bin/Release/netstandard2.0/Tabg.WorldDumper.dll");
    let source = std::env::var_os("TABG_EMBED_DUMPER")
        .map(PathBuf::from)
        .or_else(|| default.exists().then_some(default.clone()));
    println!("cargo:rerun-if-changed={}", default.display());
    let out = PathBuf::from(std::env::var("OUT_DIR").unwrap()).join("dumper.dll");
    match source {
        Some(src) => {
            println!("cargo:rerun-if-changed={}", src.display());
            std::fs::copy(&src, &out).expect("copy the dumper plugin");
        }
        None => std::fs::write(&out, b"").unwrap(),
    }
}
