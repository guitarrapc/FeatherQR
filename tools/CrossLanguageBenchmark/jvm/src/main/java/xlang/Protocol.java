package xlang;

import java.io.IOException;
import java.io.InputStream;
import java.lang.management.ManagementFactory;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.Arrays;
import java.util.Properties;
import java.util.function.LongSupplier;
import java.util.function.Supplier;

/**
 * The cross-language benchmark's protocol for the JVM CLIs: the same arguments, timing loop and JSON as FeatherQR's
 * (tools/CrossLanguageBenchmark/dotnet/cli/Protocol.cs), which is the reference implementation.
 * See .github/docs/specs/qrcode-cross-language-benchmark.md ("Protocol").
 */
public final class Protocol {
    private Protocol() {
    }

    /**
     * One case made into a call. The input is read and converted before any timing, so the timed call does only QR work.
     * {@code call} is the timed unit of work, and its value is folded into the checksum, so the work cannot be dropped. It is never 0 for a
     * call that succeeds and always 0 for one that fails, so the loop counts the calls that failed.
     * {@code describe} makes one call and returns its result as protocol JSON members: the status, then the decoded text or the encoded matrix.
     */
    public record Operation(LongSupplier call, Supplier<String> describe) {
    }

    /** Makes the operation, or returns null for one the library does not offer. */
    @FunctionalInterface
    public interface Loader {
        Operation load(String op, String symbology, String input, String ecc, String version) throws Exception;
    }

    /**
     * An input the timed call reads through a volatile field, so the JIT cannot treat it as constant and hoist the call out of the loop
     * (C2 can inline the call into the loop).
     */
    public static final class Input<T> {
        public volatile T value;

        public Input(T value) {
            this.value = value;
        }
    }

    public record Image(byte[] pixels, int width, int height) {
    }

    public static final String FAILED = "\"status\":\"failed\"";

    public static int run(String[] args, String library, Loader loader) {
        if (args.length < 4)
            return usage();
        String mode = args[0], op = args[1], symbology = args[2], input = args[3];
        String runtime = "Java " + Runtime.version() + " " + System.getProperty("java.vm.name");
        // The flags the JVM was started with (GC and heap), as the protocol's build member.
        String build = "hotspot " + String.join(" ", ManagementFactory.getRuntimeMXBean().getInputArguments());
        StringBuilder json = new StringBuilder("{\"protocol\":1,\"library\":\"").append(library)
                .append("\",\"libraryVersion\":\"").append(version(library))
                .append("\",\"runtime\":\"").append(runtime)
                .append("\",\"build\":\"").append(build)
                .append("\",\"mode\":\"").append(mode).append('"');

        Operation operation;
        try {
            operation = loader.load(op, symbology, input, option(args, "--ecc"), option(args, "--version"));
        } catch (Exception e) {
            System.err.println(e);
            return 2;
        }
        if (operation == null) {
            System.out.print(json.append(",\"status\":\"unsupported\"}\n"));
            return 0;
        }

        // Verification checks one call. A library can still fail the calls after it, for example by changing its input, so every timed call
        // that fails is counted, and the collector rejects a process with any.
        long sink = 0, failed = 0;
        switch (mode) {
            case "run" -> {
                String described = operation.describe().get();
                json.append(',').append(described);
                // An input the library cannot handle is reported, not timed: a failing call costs what failing costs.
                if (described.equals(FAILED))
                    break;
                // The JVM keeps getting faster for seconds after a .NET or native CLI is steady, and its per-call time swings by about 5 % from
                // second to second, so a JVM CLI raises both lengths to the floors clis.tsv gives it (-Dxlang.minWarmupMs, -Dxlang.minBatchMs).
                double warmupMs = Math.max(Double.parseDouble(orElse(option(args, "--warmup-ms"), "3000")), floor("xlang.minWarmupMs"));
                double batchMs = Math.max(Double.parseDouble(orElse(option(args, "--batch-ms"), "20")), floor("xlang.minBatchMs"));
                int batches = Integer.parseInt(orElse(option(args, "--batches"), "30"));
                LongSupplier call = operation.call();

                // Warm up for the stated time and at least 3 calls. The batch size comes from the warmup's second half, after the first tiers.
                long warmupNs = (long) (warmupMs * 1e6);
                long calls = 0, halfCalls = 0, halfNs = 0, elapsed;
                long start = System.nanoTime();
                do {
                    long value = call.getAsLong();
                    sink += value;
                    if (value == 0)
                        failed++;
                    calls++;
                    elapsed = System.nanoTime() - start;
                    if (halfCalls == 0 && elapsed >= warmupNs / 2) {
                        halfCalls = calls;
                        halfNs = elapsed;
                    }
                } while (calls < 3 || elapsed < warmupNs);
                double perCall = calls > halfCalls ? (double) (elapsed - halfNs) / (calls - halfCalls) : (double) elapsed / calls;
                long batchCalls = Math.max(1L, (long) (batchMs * 1e6 / perCall));

                long[] samples = new long[batches];
                for (int b = 0; b < batches; b++) {
                    long t0 = System.nanoTime();
                    for (long k = 0; k < batchCalls; k++) {
                        long value = call.getAsLong();
                        sink += value;
                        if (value == 0)
                            failed++;
                    }
                    samples[b] = System.nanoTime() - t0;
                }

                json.append(",\"warmupCalls\":").append(calls).append(",\"warmupNs\":").append(elapsed)
                        .append(",\"batchCalls\":").append(batchCalls).append(",\"batchNs\":[");
                for (int b = 0; b < batches; b++)
                    json.append(b == 0 ? "" : ",").append(samples[b]);
                json.append("],\"failedCalls\":").append(failed);
            }
            case "fixed" -> {
                String option = option(args, "--iterations");
                if (option == null)
                    return usage();
                long iterations = Long.parseLong(option);
                LongSupplier call = operation.call();
                for (long k = 0; k < iterations; k++) {
                    long value = call.getAsLong();
                    sink += value;
                    if (value == 0)
                        failed++;
                }
                json.append(",\"status\":\"ok\",\"iterations\":").append(iterations).append(",\"failedCalls\":").append(failed);
            }
            case "cold" -> json.append(',').append(operation.describe().get());
            case "noop" -> json.append(",\"status\":\"ok\"");
            default -> {
                return usage();
            }
        }

        System.out.print(json.append(",\"checksum\":\"").append(Long.toUnsignedString(sink)).append("\"}\n"));
        System.out.flush();
        return 0;
    }

    /** The fold for a decode: the text's length and last character, so it depends on the content. */
    public static long fold(String text) {
        return text == null || text.isEmpty() ? 0 : text.length() * 31L + text.charAt(text.length() - 1);
    }

    public static String decoded(String text) {
        StringBuilder json = new StringBuilder("\"status\":\"ok\",\"text\":\"");
        for (byte b : text.getBytes(StandardCharsets.UTF_8))
            json.append(Character.toUpperCase(Character.forDigit((b >> 4) & 15, 16))).append(Character.toUpperCase(Character.forDigit(b & 15, 16)));
        return json.append('"').toString();
    }

    @FunctionalInterface
    public interface Module {
        boolean isDark(int row, int col);
    }

    /** Rows top to bottom, 1 for a dark module, with whatever quiet zone the library returns. */
    public static String matrix(int width, int height, Module module) {
        StringBuilder json = new StringBuilder("\"status\":\"ok\",\"matrix\":{\"width\":").append(width).append(",\"height\":").append(height).append(",\"rows\":[");
        for (int row = 0; row < height; row++) {
            json.append(row == 0 ? "\"" : ",\"");
            for (int col = 0; col < width; col++)
                json.append(module.isDark(row, col) ? '1' : '0');
            json.append('"');
        }
        return json.append("]}").toString();
    }

    /** The payload's exact bytes as text. */
    public static String readText(String path) throws IOException {
        return new String(Files.readAllBytes(Path.of(path)), StandardCharsets.UTF_8);
    }

    /** Binary PGM (P5, maxval 255): the corpus's one image format. */
    public static Image readPgm(String path) throws IOException {
        byte[] bytes = Files.readAllBytes(Path.of(path));
        int[] position = {0};
        if (!token(bytes, position).equals("P5"))
            throw new IOException(path + " is not a binary PGM (P5)");
        int width = Integer.parseInt(token(bytes, position));
        int height = Integer.parseInt(token(bytes, position));
        if (!token(bytes, position).equals("255"))
            throw new IOException(path + ": the corpus uses maxval 255");
        // Exactly one whitespace byte separates the header from the raster.
        int start = position[0] + 1;
        if (bytes.length - start != width * height)
            throw new IOException(path + ": raster size does not match the header");
        return new Image(Arrays.copyOfRange(bytes, start, bytes.length), width, height);
    }

    private static String token(byte[] bytes, int[] position) {
        int i = position[0];
        while (i < bytes.length) {
            if (bytes[i] == '#') {
                while (i < bytes.length && bytes[i] != '\n')
                    i++;
            } else if (bytes[i] == ' ' || bytes[i] == '\t' || bytes[i] == '\n' || bytes[i] == '\r') {
                i++;
            } else {
                break;
            }
        }
        int start = i;
        while (i < bytes.length && !Character.isWhitespace(bytes[i]))
            i++;
        position[0] = i;
        return new String(bytes, start, i - start, StandardCharsets.US_ASCII);
    }

    private static String version(String library) {
        try (InputStream stream = Protocol.class.getResourceAsStream("/xlang-versions.properties")) {
            Properties properties = new Properties();
            properties.load(stream);
            return properties.getProperty(library.toLowerCase(), "unknown");
        } catch (IOException | NullPointerException e) {
            return "unknown";
        }
    }

    private static String option(String[] args, String name) {
        for (int i = 0; i + 1 < args.length; i++)
            if (args[i].equals(name))
                return args[i + 1];
        return null;
    }

    private static double floor(String property) {
        String value = System.getProperty(property);
        return value == null ? 0 : Double.parseDouble(value);
    }

    private static String orElse(String value, String fallback) {
        return value != null ? value : fallback;
    }

    private static int usage() {
        System.err.println("usage: <cli> <run|fixed|cold|noop> <encode|decode-matrix|decode-image> <qr|microqr|rmqr> <input> [--ecc E] [--version V] [--warmup-ms 3000 --batch-ms 20 --batches 30 | --iterations N]");
        return 2;
    }
}
