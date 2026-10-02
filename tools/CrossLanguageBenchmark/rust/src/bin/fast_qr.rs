//! fast_qr's CLI for the cross-language benchmark: Standard QR encode, pinned to the case's level and version.
//! fast_qr writes Standard QR only, so every other symbology and decode report unsupported.
//! The timed call is the builder from the payload to the module matrix, as a caller writes it; the builder copies the payload.

use std::hint::black_box;
use std::process::ExitCode;

use fast_qr::{ECL, QRBuilder, QRCode, Version};
use xlang_rust::{Args, FAILED, Operation, matrix, read_text};

fn main() -> ExitCode {
    xlang_rust::run("fast_qr", env!("XLANG_FAST_QR_VERSION"), load)
}

const VERSIONS: [Version; 40] = {
    use Version::*;
    [
        V01, V02, V03, V04, V05, V06, V07, V08, V09, V10, V11, V12, V13, V14, V15, V16, V17, V18,
        V19, V20, V21, V22, V23, V24, V25, V26, V27, V28, V29, V30, V31, V32, V33, V34, V35, V36,
        V37, V38, V39, V40,
    ]
};

fn load(args: &Args) -> Result<Option<Operation>, String> {
    if (args.op.as_str(), args.symbology.as_str()) != ("encode", "qr") {
        return Ok(None);
    }
    let text = read_text(&args.input)?;
    let ecl = match args.ecc.as_deref() {
        Some("L") => ECL::L,
        Some("M") => ECL::M,
        Some("Q") => ECL::Q,
        Some("H") => ECL::H,
        other => return Err(format!("encode needs --ecc L, M, Q or H, got {other:?}")),
    };
    let version = args
        .version
        .as_deref()
        .and_then(|v| v.parse::<usize>().ok())
        .filter(|v| (1..=40).contains(v))
        .map(|v| VERSIONS[v - 1])
        .ok_or_else(|| format!("encode needs --version 1 to 40, got {:?}", args.version))?;

    let described = text.clone();
    Ok(Some(Operation {
        call: Box::new(move || encode(black_box(&text), ecl, version).map_or(0, |qr| fold(&qr))),
        describe: Box::new(move || {
            encode(&described, ecl, version).map_or_else(
                || FAILED.to_string(),
                |qr| matrix(qr.size, qr.size, |row, col| qr[row][col].value()),
            )
        }),
    }))
}

fn encode(text: &str, ecl: ECL, version: Version) -> Option<QRCode> {
    QRBuilder::new(text.as_bytes())
        .ecl(ecl)
        .version(version)
        .build()
        .ok()
}

/// The matrix size and its centre module, so the fold depends on the content.
fn fold(qr: &QRCode) -> u64 {
    qr.size as u64 * 2 + u64::from(qr[qr.size / 2][qr.size / 2].value())
}
