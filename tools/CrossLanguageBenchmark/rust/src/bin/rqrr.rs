//! rqrr's CLI for the cross-language benchmark: Standard QR decode, from a bare module matrix or from 8-bit grey pixels.
//! rqrr reads Standard QR only, so every other symbology and encode report unsupported.

use std::hint::black_box;
use std::process::ExitCode;

use rqrr::{BitGrid, Grid, PreparedImage};
use xlang_rust::{Args, FAILED, Operation, decoded, fold_text, read_pgm};

fn main() -> ExitCode {
    xlang_rust::run("rqrr", env!("XLANG_RQRR_VERSION"), load)
}

fn load(args: &Args) -> Result<Option<Operation>, String> {
    match (args.op.as_str(), args.symbology.as_str()) {
        ("decode-matrix", "qr") => {
            let (pixels, width, height) = read_pgm(&args.input)?;
            if width != height {
                return Err(format!(
                    "{} is {width}x{height}; Standard QR is square",
                    args.input
                ));
            }
            let modules: Vec<bool> = pixels.iter().map(|&p| p < 128).collect();
            let described = modules.clone();
            Ok(Some(Operation {
                call: Box::new(move || {
                    decode_matrix(black_box(&modules), width).map_or(0, |text| fold_text(&text))
                }),
                describe: Box::new(move || {
                    decode_matrix(&described, width)
                        .map_or_else(|| FAILED.to_string(), |text| decoded(&text))
                }),
            }))
        }
        ("decode-image", "qr") => {
            let (pixels, width, height) = read_pgm(&args.input)?;
            let described = pixels.clone();
            Ok(Some(Operation {
                call: Box::new(move || {
                    decode_image(black_box(&pixels), width, height)
                        .map_or(0, |text| fold_text(&text))
                }),
                describe: Box::new(move || {
                    decode_image(&described, width, height)
                        .map_or_else(|| FAILED.to_string(), |text| decoded(&text))
                }),
            }))
        }
        _ => Ok(None),
    }
}

/// The modules as rqrr's grid trait sees them, borrowed rather than copied.
struct Modules<'a> {
    bits: &'a [bool],
    size: usize,
}

impl BitGrid for Modules<'_> {
    fn size(&self) -> usize {
        self.size
    }

    fn bit(&self, y: usize, x: usize) -> bool {
        self.bits[y * self.size + x]
    }
}

fn decode_matrix(bits: &[bool], size: usize) -> Option<String> {
    Grid::new(Modules { bits, size })
        .decode()
        .ok()
        .map(|(_, text)| text)
}

/// rqrr's grey entry point: it copies and binarizes the image, finds every grid, and here the first grid that decodes wins.
fn decode_image(pixels: &[u8], width: usize, height: usize) -> Option<String> {
    let mut prepared =
        PreparedImage::prepare_from_greyscale(width, height, |x, y| pixels[y * width + x]);
    prepared
        .detect_grids()
        .into_iter()
        .find_map(|grid| grid.decode().ok().map(|(_, text)| text))
}
