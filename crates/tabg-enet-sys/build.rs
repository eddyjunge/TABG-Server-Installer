fn main() {
    println!("cargo:rerun-if-changed=vendor/enet.c");
    println!("cargo:rerun-if-changed=vendor/enet.h");

    cc::Build::new()
        .file("vendor/enet.c")
        .include("vendor")
        .warnings(false)
        .compile("enet");

    if std::env::var("CARGO_CFG_TARGET_OS").as_deref() == Ok("windows") {
        println!("cargo:rustc-link-lib=ws2_32");
        println!("cargo:rustc-link-lib=winmm");
    }
}
