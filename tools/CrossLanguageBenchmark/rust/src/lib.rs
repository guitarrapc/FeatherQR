//! The cross-language benchmark's protocol for the Rust CLIs: the same arguments, timing loop and JSON as
//! FeatherQR's CLI (tools/CrossLanguageBenchmark/dotnet/cli/Program.cs), which is the reference implementation.
//! See .github/docs/specs/qrcode-cross-language-benchmark.md ("Protocol").

use std::process::ExitCode;
use std::time::{Duration, Instant};

pub struct Args {
    pub mode: String,
    pub op: String,
    pub symbology: String,
    pub input: String,
    pub ecc: Option<String>,
    pub version: Option<String>,
    options: Vec<String>,
}

impl Args {
    fn option(&self, name: &str) -> Option<&str> {
        let i = self.options.iter().position(|o| o == name)?;
        self.options.get(i + 1).map(String::as_str)
    }
}

/// One case made into a call. The input is read and converted before any timing, so the timed call does only QR work.
pub struct Operation {
    /// The timed unit of work. Its value is folded into the checksum, so the work cannot be dropped. It is never 0 for a call that
    /// succeeds and always 0 for one that fails, so the loop counts the calls that failed.
    pub call: Box<dyn FnMut() -> u64>,
    /// Makes one call and returns its result as protocol JSON members: the status, then the decoded text or the encoded matrix.
    pub describe: Box<dyn FnMut() -> String>,
}

/// Runs the protocol for one library. `load` returns `Ok(None)` for an operation the library does not offer.
pub fn run(
    library: &str,
    library_version: &str,
    load: impl FnOnce(&Args) -> Result<Option<Operation>, String>,
) -> ExitCode {
    let raw: Vec<String> = std::env::args().skip(1).collect();
    if raw.len() < 4 {
        return usage();
    }
    let mut args = Args {
        mode: raw[0].clone(),
        op: raw[1].clone(),
        symbology: raw[2].clone(),
        input: raw[3].clone(),
        ecc: None,
        version: None,
        options: raw[4..].to_vec(),
    };
    args.ecc = args.option("--ecc").map(str::to_string);
    args.version = args.option("--version").map(str::to_string);

    let mut json = format!(
        "{{\"protocol\":1,\"library\":\"{library}\",\"libraryVersion\":\"{library_version}\",\"runtime\":\"{}\",\"build\":\"{}\",\"mode\":\"{}\"",
        env!("XLANG_RUSTC"),
        env!("XLANG_RUSTFLAGS"),
        args.mode
    );

    let mut operation = match load(&args) {
        Ok(Some(operation)) => operation,
        Ok(None) => {
            print!("{json},\"status\":\"unsupported\"}}\n");
            return ExitCode::SUCCESS;
        }
        Err(message) => {
            eprintln!("{message}");
            return ExitCode::from(2);
        }
    };

    // Verification checks one call. A library can still fail the calls after it, for example by changing its input, so every timed
    // call that fails is counted, and the collector rejects a process with any.
    let (mut sink, mut failed) = (0u64, 0u64);
    let mut tally = |value: u64| {
        sink = sink.wrapping_add(value);
        failed += u64::from(value == 0);
    };
    match args.mode.as_str() {
        "run" => 'run: {
            let described = (operation.describe)();
            json.push(',');
            json.push_str(&described);
            // An input the library cannot handle is reported, not timed: a failing call costs what failing costs.
            if described == FAILED {
                break 'run;
            }
            let warmup_ms: f64 = number(&args, "--warmup-ms", 3000.0);
            let batch_ms: f64 = number(&args, "--batch-ms", 20.0);
            let batches = number(&args, "--batches", 30.0) as usize;
            let call = &mut operation.call;

            // Warm up for the stated time and at least 3 calls. The batch size comes from the warmup's second half.
            let warmup = Duration::from_secs_f64(warmup_ms / 1000.0);
            let (mut calls, mut half_calls, mut half_elapsed) = (0u64, 0u64, Duration::ZERO);
            let start = Instant::now();
            let elapsed = loop {
                tally(call());
                calls += 1;
                let elapsed = start.elapsed();
                if half_calls == 0 && elapsed >= warmup / 2 {
                    (half_calls, half_elapsed) = (calls, elapsed);
                }
                if calls >= 3 && elapsed >= warmup {
                    break elapsed;
                }
            };
            let per_call = if calls > half_calls {
                (elapsed - half_elapsed).as_secs_f64() / (calls - half_calls) as f64
            } else {
                elapsed.as_secs_f64() / calls as f64
            };
            let batch_calls = ((batch_ms / 1000.0 / per_call) as u64).max(1);

            let mut samples = vec![0u128; batches];
            for sample in samples.iter_mut() {
                let t0 = Instant::now();
                for _ in 0..batch_calls {
                    tally(call());
                }
                *sample = t0.elapsed().as_nanos();
            }

            let samples: Vec<String> = samples.iter().map(u128::to_string).collect();
            json.push_str(&format!(
                ",\"warmupCalls\":{calls},\"warmupNs\":{},\"batchCalls\":{batch_calls},\"batchNs\":[{}],\"failedCalls\":{failed}",
                elapsed.as_nanos(),
                samples.join(",")
            ));
        }
        "fixed" => {
            let Some(iterations) = args
                .option("--iterations")
                .and_then(|v| v.parse::<u64>().ok())
            else {
                return usage();
            };
            let call = &mut operation.call;
            for _ in 0..iterations {
                tally(call());
            }
            json.push_str(&format!(
                ",\"status\":\"ok\",\"iterations\":{iterations},\"failedCalls\":{failed}"
            ));
        }
        "cold" => {
            json.push(',');
            json.push_str(&(operation.describe)());
        }
        "noop" => json.push_str(",\"status\":\"ok\""),
        _ => return usage(),
    }

    print!("{json},\"checksum\":\"{}\"}}\n", std::hint::black_box(sink));
    ExitCode::SUCCESS
}

fn number(args: &Args, name: &str, fallback: f64) -> f64 {
    args.option(name)
        .and_then(|v| v.parse().ok())
        .unwrap_or(fallback)
}

fn usage() -> ExitCode {
    eprintln!(
        "usage: <cli> <run|fixed|cold|noop> <encode|decode-matrix|decode-image> <qr|microqr|rmqr> <input> [--ecc E] [--version V] [--warmup-ms 3000 --batch-ms 20 --batches 30 | --iterations N]"
    );
    ExitCode::from(2)
}

pub const FAILED: &str = "\"status\":\"failed\"";

/// The fold for a decode: the text's length and last character, so it depends on the content.
pub fn fold_text(text: &str) -> u64 {
    text.len() as u64 * 31 + text.chars().last().map_or(0, |c| c as u64)
}

pub fn decoded(text: &str) -> String {
    let hex: String = text.bytes().map(|b| format!("{b:02X}")).collect();
    format!("\"status\":\"ok\",\"text\":\"{hex}\"")
}

/// Rows top to bottom, 1 for a dark module, with whatever quiet zone the library returns.
pub fn matrix(width: usize, height: usize, is_dark: impl Fn(usize, usize) -> bool) -> String {
    let rows: Vec<String> = (0..height)
        .map(|row| {
            format!(
                "\"{}\"",
                (0..width)
                    .map(|col| if is_dark(row, col) { '1' } else { '0' })
                    .collect::<String>()
            )
        })
        .collect();
    format!(
        "\"status\":\"ok\",\"matrix\":{{\"width\":{width},\"height\":{height},\"rows\":[{}]}}",
        rows.join(",")
    )
}

/// The payload's exact bytes as text.
pub fn read_text(path: &str) -> Result<String, String> {
    let bytes = std::fs::read(path).map_err(|e| format!("{path}: {e}"))?;
    String::from_utf8(bytes).map_err(|e| format!("{path}: {e}"))
}

/// Binary PGM (P5, maxval 255): the corpus's one image format.
pub fn read_pgm(path: &str) -> Result<(Vec<u8>, usize, usize), String> {
    let bytes = std::fs::read(path).map_err(|e| format!("{path}: {e}"))?;
    let mut position = 0;
    let mut token = || -> Result<String, String> {
        loop {
            match bytes.get(position) {
                Some(b'#') => {
                    while bytes.get(position).is_some_and(|&b| b != b'\n') {
                        position += 1;
                    }
                }
                Some(b' ' | b'\t' | b'\n' | b'\r') => position += 1,
                Some(_) => break,
                None => return Err(format!("{path}: header ends early")),
            }
        }
        let start = position;
        while bytes
            .get(position)
            .is_some_and(|b| !b.is_ascii_whitespace())
        {
            position += 1;
        }
        Ok(String::from_utf8_lossy(&bytes[start..position]).into_owned())
    };
    if token()? != "P5" {
        return Err(format!("{path} is not a binary PGM (P5)"));
    }
    let width: usize = token()?.parse().map_err(|_| format!("{path}: bad width"))?;
    let height: usize = token()?
        .parse()
        .map_err(|_| format!("{path}: bad height"))?;
    if token()? != "255" {
        return Err(format!("{path}: the corpus uses maxval 255"));
    }
    // Exactly one whitespace byte separates the header from the raster.
    let raster = &bytes[position + 1..];
    if raster.len() != width * height {
        return Err(format!(
            "{path} holds {} pixel bytes for {width}x{height}",
            raster.len()
        ));
    }
    Ok((raster.to_vec(), width, height))
}
