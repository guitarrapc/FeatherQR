//! The qrcode crate's CLI for the cross-language benchmark: Standard QR and Micro QR encode, pinned to the case's level and version.
//! The crate writes no rMQR and does not decode, so the rest report unsupported.
//! The timed call is `QrCode::with_version` from the payload's bytes to the module matrix, as a caller writes it: the crate splits the
//! bytes into segments by its own optimiser, which takes any byte pair in Shift JIS's double-byte ranges for Kanji, and picks the mask.

use std::hint::black_box;
use std::process::ExitCode;

use qrcode::{Color, EcLevel, QrCode, Version};
use xlang_rust::{Args, FAILED, Operation, matrix, read_text};

fn main() -> ExitCode {
    xlang_rust::run("qrcode", env!("XLANG_QRCODE_VERSION"), load)
}

fn load(args: &Args) -> Result<Option<Operation>, String> {
    let version = match (args.op.as_str(), args.symbology.as_str()) {
        ("encode", "qr") => args
            .version
            .as_deref()
            .and_then(|v| v.parse::<i16>().ok())
            .filter(|v| (1..=40).contains(v))
            .map(Version::Normal)
            .ok_or_else(|| format!("encode needs --version 1 to 40, got {:?}", args.version))?,
        ("encode", "microqr") => args
            .version
            .as_deref()
            .and_then(|v| v.strip_prefix('M')?.parse::<i16>().ok())
            .filter(|v| (1..=4).contains(v))
            .map(Version::Micro)
            .ok_or_else(|| format!("encode needs --version M1 to M4, got {:?}", args.version))?,
        _ => return Ok(None),
    };
    let level = match args.ecc.as_deref() {
        Some("L") => EcLevel::L,
        Some("M") => EcLevel::M,
        Some("Q") => EcLevel::Q,
        Some("H") => EcLevel::H,
        other => return Err(format!("encode needs --ecc L, M, Q or H, got {other:?}")),
    };
    let text = read_text(&args.input)?;

    let described = text.clone();
    Ok(Some(Operation {
        call: Box::new(move || encode(black_box(&text), version, level).map_or(0, |qr| fold(&qr))),
        describe: Box::new(move || {
            encode(&described, version, level).map_or_else(
                || FAILED.to_string(),
                |qr| matrix(qr.width(), qr.width(), |row, col| qr[(col, row)] == Color::Dark),
            )
        }),
    }))
}

fn encode(text: &str, version: Version, level: EcLevel) -> Option<QrCode> {
    QrCode::with_version(text.as_bytes(), version, level).ok()
}

/// The matrix size and its centre module, so the fold depends on the content.
fn fold(qr: &QrCode) -> u64 {
    let width = qr.width();
    width as u64 * 2 + u64::from(qr[(width / 2, width / 2)] == Color::Dark)
}
