//! `tabg` — host a TABG server for your friends.
//!
//! This is the M0 skeleton. See docs/DESIGN.md for the plan; commands are added
//! only when they actually work.

use clap::Parser;

#[derive(Parser)]
#[command(name = "tabg", version, about, long_about = None)]
struct Cli {}

fn main() {
    let _cli = Cli::parse();
    println!(
        "tabg {} — early development, nothing to run yet. See docs/DESIGN.md.",
        env!("CARGO_PKG_VERSION")
    );
}
