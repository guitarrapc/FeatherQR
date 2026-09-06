using System.Runtime.CompilerServices;

namespace FeatherQR.Internals.StandardQR;

internal static class QRCodeConstants
{
    private static readonly Lazy<IReadOnlyList<AlignmentPattern>> alignmentPatternTable = new(() => CreateAlignmentPatternTable());
    private static readonly Lazy<IReadOnlyList<ECCInfo>> capacityECCTable = new(() => CreateCapacityECCTable());
    private static readonly Lazy<IReadOnlyList<VersionInfo>> capacityTable = new(() => CreateCapacityTable());

    public static IReadOnlyList<AlignmentPattern> AlignmentPatternTable => alignmentPatternTable.Value;
    public static IReadOnlyList<ECCInfo> CapacityECCTable => capacityECCTable.Value;
    public static IReadOnlyList<VersionInfo> CapacityTable => capacityTable.Value;

    // Maximum data capacity for each QR code version (1-40) and error correction level (L,M,Q,H)
    // Array structure: [version-1][eccLevel][encodingMode]
    // - 1600 elements total (40 versions × 4 ECC levels × 4 encoding modes)
    // - Each 16-element block represents one version's capacities
    // - Within each block: [L-Numeric, L-Alpha, L-Byte, L-Kanji, M-Numeric, M-Alpha, M-Byte, M-Kanji, Q-..., H-...]
    // - Index calculation: (version-1) × 16 + eccLevel × 4 + encodingMode
    // - Example: Version 1, ECC-M, Alphanumeric = 0×16 + 1×4 + 1 = capacityBaseValues[5] = 20 characters
    // Based on ISO/IEC 18004 Table 7-11
    private static readonly int[] capacityBaseValues = [
        // ECC Level L: Numeric, Alphanumeric, Byte, Kanji
        // ECC Level M: Numeric, Alphanumeric, Byte, Kanji
        // ECC Level Q: Numeric, Alphanumeric, Byte, Kanji
        // ECC Level H: Numeric, Alphanumeric, Byte, Kanji
        // Version 1 (21×21 modules)
        41, 25, 17, 10,
        34, 20, 14, 8,
        27, 16, 11, 7,
        17, 10, 7, 4,
        // Version 2 (25×25 modules)
        77, 47, 32, 20,
        63, 38, 26, 16,
        48, 29, 20, 12,
        34, 20, 14, 8,
        // Version 3
        127, 77, 53, 32,
        101, 61, 42, 26,
        77, 47, 32, 20,
        58, 35, 24, 15,
        // Version 4
        187, 114, 78, 48,
        149, 90, 62, 38,
        111, 67, 46, 28,
        82, 50, 34, 21,
        // Version 5
        255, 154, 106, 65,
        202, 122, 84, 52,
        144, 87, 60, 37,
        106, 64, 44, 27,
        // Version 6
        322, 195, 134, 82,
        255, 154, 106, 65,
        178, 108, 74, 45,
        139, 84, 58, 36,
        // Version 7
        370, 224, 154, 95,
        293, 178, 122, 75,
        207, 125, 86, 53,
        154, 93, 64, 39,
        // Version 8
        461, 279, 192, 118,
        365, 221, 152, 93,
        259, 157, 108, 66,
        202, 122, 84, 52,
        // Version 9
        552, 335, 230, 141,
        432, 262, 180, 111,
        312, 189, 130, 80,
        235, 143, 98, 60,
        // Version 10
        652, 395, 271, 167,
        513, 311, 213, 131,
        364, 221, 151, 93,
        288, 174, 119, 74,
        // Version 11
        772, 468, 321, 198,
        604, 366, 251, 155,
        427, 259, 177, 109,
        331, 200, 137, 85,
        // Version 12
        883, 535, 367, 226,
        691, 419, 287, 177,
        489, 296, 203, 125,
        374, 227, 155, 96,
        // Version 13
        1022, 619, 425, 262,
        796, 483, 331, 204,
        580, 352, 241, 149,
        427, 259, 177, 109,
        // Version 14
        1101, 667, 458, 282,
        871, 528, 362, 223,
        621, 376, 258, 159,
        468, 283, 194, 120,
        // Version 15
        1250, 758, 520, 320,
        991, 600, 412, 254,
        703, 426, 292, 180,
        530, 321, 220, 136,
        // Version 16
        1408, 854, 586, 361,
        1082, 656, 450, 277,
        775, 470, 322, 198,
        602, 365, 250, 154,
        // Version 17
        1548, 938, 644, 397,
        1212, 734, 504, 310,
        876, 531, 364, 224,
        674, 408, 280, 173,
        // Version 18
        1725, 1046, 718, 442,
        1346, 816, 560, 345,
        948, 574, 394, 243,
        746, 452, 310, 191,
        // Version 19
        1903, 1153, 792, 488,
        1500, 909, 624, 384,
        1063, 644, 442, 272,
        813, 493, 338, 208,
        // Version 20
        2061, 1249, 858, 528,
        1600, 970, 666, 410,
        1159, 702, 482, 297,
        919, 557, 382, 235,
        // Version 21
        2232, 1352, 929, 572,
        1708, 1035, 711, 438,
        1224, 742, 509, 314,
        969, 587, 403, 248,
        // Version 22
        2409, 1460, 1003, 618,
        1872, 1134, 779, 480,
        1358, 823, 565, 348,
        1056, 640, 439, 270,
        // Version 23
        2620, 1588, 1091, 672,
        2059, 1248, 857, 528,
        1468, 890, 611, 376,
        1108, 672, 461, 284,
        // Version 24
        2812, 1704, 1171, 721,
        2188, 1326, 911, 561,
        1588, 963, 661, 407,
        1228, 744, 511, 315,
        // Version 25
        3057, 1853, 1273, 784,
        2395, 1451, 997, 614,
        1718, 1041, 715, 440,
        1286, 779, 535, 330,
        // Version 26
        3283, 1990, 1367, 842,
        2544, 1542, 1059, 652,
        1804, 1094, 751, 462,
        1425, 864, 593, 365,
        // Version 27
        3517, 2132, 1465, 902,
        2701, 1637, 1125, 692,
        1933, 1172, 805, 496,
        1501, 910, 625, 385,
        // Version 28
        3669, 2223, 1528, 940,
        2857, 1732, 1190, 732,
        2085, 1263, 868, 534,
        1581, 958, 658, 405,
        // Version 29
        3909, 2369, 1628, 1002,
        3035, 1839, 1264, 778,
        2181, 1322, 908, 559,
        1677, 1016, 698, 430,
        // Version 30
        4158, 2520, 1732, 1066,
        3289, 1994, 1370, 843,
        2358, 1429, 982, 604,
        1782, 1080, 742, 457,
        // Version 31
        4417, 2677, 1840, 1132,
        3486, 2113, 1452, 894,
        2473, 1499, 1030, 634,
        1897, 1150, 790, 486,
        // Version 32
        4686, 2840, 1952, 1201,
        3693, 2238, 1538, 947,
        2670, 1618, 1112, 684,
        2022, 1226, 842, 518,
        // Version 33
        4965, 3009, 2068, 1273,
        3909, 2369, 1628, 1002,
        2805, 1700, 1168, 719,
        2157, 1307, 898, 553,
        // Version 34
        5253, 3183, 2188, 1347,
        4134, 2506, 1722, 1060,
        2949, 1787, 1228, 756,
        2301, 1394, 958, 590,
        // Version 35
        5529, 3351, 2303, 1417,
        4343, 2632, 1809, 1113,
        3081, 1867, 1283, 790,
        2361, 1431, 983, 605,
        // Version 36
        5836, 3537, 2431, 1496,
        4588, 2780, 1911, 1176,
        3244, 1966, 1351, 832,
        2524, 1530, 1051, 647,
        // Version 37
        6153, 3729, 2563, 1577,
        4775, 2894, 1989, 1224,
        3417, 2071, 1423, 876,
        2625, 1591, 1093, 673,
        // Version 38
        6479, 3927, 2699, 1661,
        5039, 3054, 2099, 1292,
        3599, 2181, 1499, 923,
        2735, 1658, 1139, 701,
        // Version 39
        6743, 4087, 2809, 1729,
        5313, 3220, 2213, 1362,
        3791, 2298, 1579, 972,
        2927, 1774, 1219, 750,
        // Version 40
        7089, 4296, 2953, 1817,
        5596, 3391, 2331, 1435,
        3993, 2420, 1663, 1024,
        3057, 1852, 1273, 784,
    ];
    public static ReadOnlySpan<int> CapacityBaseValues => capacityBaseValues;

    // Error correction codewords configuration for each version and ECC level
    // Array structure: [version-1][eccLevel][6 parameters]
    // - 960 elements total (40 versions × 4 ECC levels × 6 parameters)
    // - Each 24-element block represents one version's ECC configurations
    // - Parameters per ECC level (6 values):
    //   [0] totalDataCodewords
    //   [1] eccPerBlock
    //   [2] blocksInGroup1
    //   [3] codewordsInGroup1
    //   [4] blocksInGroup2
    //   [5] codewordsInGroup2
    // Based on ISO/IEC 18004 Table 9
    private static readonly int[] capacityECCBaseValues = [
        // ECC Level L: Numeric, Alphanumeric, Byte, Kanji
        // ECC Level M: Numeric, Alphanumeric, Byte, Kanji
        // ECC Level Q: Numeric, Alphanumeric, Byte, Kanji
        // ECC Level H: Numeric, Alphanumeric, Byte, Kanji
        // Version 1
        19, 7, 1, 19, 0, 0,
        16, 10, 1, 16, 0, 0,
        13, 13, 1, 13, 0, 0,
        9, 17, 1, 9, 0, 0,
        // Version 2
        34, 10, 1, 34, 0, 0,
        28, 16, 1, 28, 0, 0,
        22, 22, 1, 22, 0, 0,
        16, 28, 1, 16, 0, 0,
        // Version 3
        55, 15, 1, 55, 0, 0,
        44, 26, 1, 44, 0, 0,
        34, 18, 2, 17, 0, 0,
        26, 22, 2, 13, 0, 0,
        // Version 4
        80, 20, 1, 80, 0, 0,
        64, 18, 2, 32, 0, 0,
        48, 26, 2, 24, 0, 0,
        36, 16, 4, 9, 0, 0,
        // Version 5
        108, 26, 1, 108, 0, 0,
        86, 24, 2, 43, 0, 0,
        62, 18, 2, 15, 2, 16,
        46, 22, 2, 11, 2, 12,
        // Version 6
        136, 18, 2, 68, 0, 0,
        108, 16, 4, 27, 0, 0,
        76, 24, 4, 19, 0, 0,
        60, 28, 4, 15, 0, 0,
        // Version 7
        156, 20, 2, 78, 0, 0,
        124, 18, 4, 31, 0, 0,
        88, 18, 2, 14, 4, 15,
        66, 26, 4, 13, 1, 14,
        // Version 8
        194, 24, 2, 97, 0, 0,
        154, 22, 2, 38, 2, 39,
        110, 22, 4, 18, 2, 19,
        86, 26, 4, 14, 2, 15,
        // Version 9
        232, 30, 2, 116, 0, 0,
        182, 22, 3, 36, 2, 37,
        132, 20, 4, 16, 4, 17,
        100, 24, 4, 12, 4, 13,
        // Version 10
        274, 18, 2, 68, 2, 69,
        216, 26, 4, 43, 1, 44,
        154, 24, 6, 19, 2, 20,
        122, 28, 6, 15, 2, 16,
        // Version 11
        324, 20, 4, 81, 0, 0,
        254, 30, 1, 50, 4, 51,
        180, 28, 4, 22, 4, 23,
        140, 24, 3, 12, 8, 13,
        // Version 12
        370, 24, 2, 92, 2, 93,
        290, 22, 6, 36, 2, 37,
        206, 26, 4, 20, 6, 21,
        158, 28, 7, 14, 4, 15,
        // Version 13
        428, 26, 4, 107, 0, 0,
        334, 22, 8, 37, 1, 38,
        244, 24, 8, 20, 4, 21,
        180, 22, 12, 11, 4, 12,
        // Version 14
        461, 30, 3, 115, 1, 116,
        365, 24, 4, 40, 5, 41,
        261, 20, 11, 16, 5, 17,
        197, 24, 11, 12, 5, 13,
        // Version 15
        523, 22, 5, 87, 1, 88,
        415, 24, 5, 41, 5, 42,
        295, 30, 5, 24, 7, 25,
        223, 24, 11, 12, 7, 13,
        // Version 16
        589, 24, 5, 98, 1, 99,
        453, 28, 7, 45, 3, 46,
        325, 24, 15, 19, 2, 20,
        253, 30, 3, 15, 13, 16,
        // Version 17
        647, 28, 1, 107, 5, 108,
        507, 28, 10, 46, 1, 47,
        367, 28, 1, 22, 15, 23,
        283, 28, 2, 14, 17, 15,
        // Version 18
        721, 30, 5, 120, 1, 121,
        563, 26, 9, 43, 4, 44,
        397, 28, 17, 22, 1, 23,
        313, 28, 2, 14, 19, 15,
        // Version 19
        795, 28, 3, 113, 4, 114,
        627, 26, 3, 44, 11, 45,
        445, 26, 17, 21, 4, 22,
        341, 26, 9, 13, 16, 14,
        // Version 20
        861, 28, 3, 107, 5, 108,
        669, 26, 3, 41, 13, 42,
        485, 30, 15, 24, 5, 25,
        385, 28, 15, 15, 10, 16,
        // Version 21
        932, 28, 4, 116, 4, 117,
        714, 26, 17, 42, 0, 0,
        512, 28, 17, 22, 6, 23,
        406, 30, 19, 16, 6, 17,
        // Version 22
        1006, 28, 2, 111, 7, 112,
        782, 28, 17, 46, 0, 0,
        568, 30, 7, 24, 16, 25,
        442, 24, 34, 13, 0, 0,
        // Version 23
        1094, 30, 4, 121, 5, 122,
        860, 28, 4, 47, 14, 48,
        614, 30, 11, 24, 14, 25,
        464, 30, 16, 15, 14, 16,
        // Version 24
        1174, 30, 6, 117, 4, 118,
        914, 28, 6, 45, 14, 46,
        664, 30, 11, 24, 16, 25,
        514, 30, 30, 16, 2, 17,
        // Version 25
        1276, 26, 8, 106, 4, 107,
        1000, 28, 8, 47, 13, 48,
        718, 30, 7, 24, 22, 25,
        538, 30, 22, 15, 13, 16,
        // Version 26
        1370, 28, 10, 114, 2, 115,
        1062, 28, 19, 46, 4, 47,
        754, 28, 28, 22, 6, 23,
        596, 30, 33, 16, 4, 17,
        // Version 27
        1468, 30, 8, 122, 4, 123,
        1128, 28, 22, 45, 3, 46,
        808, 30, 8, 23, 26, 24,
        628, 30, 12, 15, 28, 16,
        // Version 28
        1531, 30, 3, 117, 10, 118,
        1193, 28, 3, 45, 23, 46,
        871, 30, 4, 24, 31, 25,
        661, 30, 11, 15, 31, 16,
        // Version 29
        1631, 30, 7, 116, 7, 117,
        1267, 28, 21, 45, 7, 46,
        911, 30, 1, 23, 37, 24,
        701, 30, 19, 15, 26, 16,
        // Version 30
        1735, 30, 5, 115, 10, 116,
        1373, 28, 19, 47, 10, 48,
        985, 30, 15, 24, 25, 25,
        745, 30, 23, 15, 25, 16,
        // Version 31
        1843, 30, 13, 115, 3, 116,
        1455, 28, 2, 46, 29, 47,
        1033, 30, 42, 24, 1, 25,
        793, 30, 23, 15, 28, 16,
        // Version 32
        1955, 30, 17, 115, 0, 0,
        1541, 28, 10, 46, 23, 47,
        1115, 30, 10, 24, 35, 25,
        845, 30, 19, 15, 35, 16,
        // Version 33
        2071, 30, 17, 115, 1, 116,
        1631, 28, 14, 46, 21, 47,
        1171, 30, 29, 24, 19, 25,
        901, 30, 11, 15, 46, 16,
        // Version 34
        2191, 30, 13, 115, 6, 116,
        1725, 28, 14, 46, 23, 47,
        1231, 30, 44, 24, 7, 25,
        961, 30, 59, 16, 1, 17,
        // Version 35
        2306, 30, 12, 121, 7, 122,
        1812, 28, 12, 47, 26, 48,
        1286, 30, 39, 24, 14, 25,
        986, 30, 22, 15, 41, 16,
        // Version 36
        2434, 30, 6, 121, 14, 122,
        1914, 28, 6, 47, 34, 48,
        1354, 30, 46, 24, 10, 25,
        1054, 30, 2, 15, 64, 16,
        // Version 37
        2566, 30, 17, 122, 4, 123,
        1992, 28, 29, 46, 14, 47,
        1426, 30, 49, 24, 10, 25,
        1096, 30, 24, 15, 46, 16,
        // Version 38
        2702, 30, 4, 122, 18, 123,
        2102, 28, 13, 46, 32, 47,
        1502, 30, 48, 24, 14, 25,
        1142, 30, 42, 15, 32, 16,
        // Version 39
        2812, 30, 20, 117, 4, 118,
        2216, 28, 40, 47, 7, 48,
        1582, 30, 43, 24, 22, 25,
        1222, 30, 10, 15, 67, 16,
        // Version 40
        2956, 30, 19, 118, 6, 119,
        2334, 28, 18, 47, 31, 48,
        1666, 30, 34, 24, 34, 25,
        1276, 30, 20, 15, 61, 16
    ];
    public static ReadOnlySpan<int> CapacityECCBaseValues => capacityECCBaseValues;

    // Alignment pattern positions for each QR code version
    // Array structure: 7 positions per version × 40 versions = 280 elements
    // - Version 1: No alignment patterns (all zeros)
    // - Version 2+: Positions where alignment patterns should be placed
    // - 0 indicates no pattern at that position
    // Based on ISO/IEC 18004 Annex E
    private static readonly int[] alignmentPatternBaseValues = [
        0, 0, 0, 0, 0, 0, 0,
        6, 18, 0, 0, 0, 0, 0,
        6, 22, 0, 0, 0, 0, 0,
        6, 26, 0, 0, 0, 0, 0,
        6, 30, 0, 0, 0, 0, 0,
        6, 34, 0, 0, 0, 0, 0,
        6, 22, 38, 0, 0, 0, 0,
        6, 24, 42, 0, 0, 0, 0,
        6, 26, 46, 0, 0, 0, 0,
        6, 28, 50, 0, 0, 0, 0,
        6, 30, 54, 0, 0, 0, 0,
        6, 32, 58, 0, 0, 0, 0,
        6, 34, 62, 0, 0, 0, 0,
        6, 26, 46, 66, 0, 0, 0,
        6, 26, 48, 70, 0, 0, 0,
        6, 26, 50, 74, 0, 0, 0,
        6, 30, 54, 78, 0, 0, 0,
        6, 30, 56, 82, 0, 0, 0,
        6, 30, 58, 86, 0, 0, 0,
        6, 34, 62, 90, 0, 0, 0,
        6, 28, 50, 72, 94, 0, 0,
        6, 26, 50, 74, 98, 0, 0,
        6, 30, 54, 78, 102, 0, 0,
        6, 28, 54, 80, 106, 0, 0,
        6, 32, 58, 84, 110, 0, 0,
        6, 30, 58, 86, 114, 0, 0,
        6, 34, 62, 90, 118, 0, 0,
        6, 26, 50, 74, 98, 122, 0,
        6, 30, 54, 78, 102, 126, 0,
        6, 26, 52, 78, 104, 130, 0,
        6, 30, 56, 82, 108, 134, 0,
        6, 34, 60, 86, 112, 138, 0,
        6, 30, 58, 86, 114, 142, 0,
        6, 34, 62, 90, 118, 146, 0,
        6, 30, 54, 78, 102, 126, 150,
        6, 24, 50, 76, 102, 128, 154,
        6, 28, 54, 80, 106, 132, 158,
        6, 32, 58, 84, 110, 136, 162,
        6, 26, 54, 82, 110, 138, 166,
        6, 30, 58, 86, 114, 142, 170
    ];
    public static ReadOnlySpan<int> AlignmentPatternBaseValues => alignmentPatternBaseValues;

    // Number of remainder bits for each QR code version (1-40)
    // These bits are added as padding after all data and ECC codewords
    // Values range from 0 to 7 bits depending on version
    // Based on ISO/IEC 18004 Table 1
    private static readonly int[] remainderBits = [
        0, 7, 7, 7, 7, 7,
        0, 0, 0, 0, 0, 0, 0,
        3, 3, 3, 3, 3, 3, 3,
        4, 4, 4, 4, 4, 4, 4,
        3, 3, 3, 3, 3, 3, 3,
        0, 0, 0, 0, 0, 0
    ];

    /// <summary>
    /// Gets the number of remainder bits for a specific QR code version.
    /// </summary>
    /// <param name="version">The QR code version (1-40) for which to retrieve the remainder bits.</param>
    public static int GetRemainderBits(int version)
    {
        return remainderBits.AsSpan()[version - 1];
    }

    // Binary and Decimal Conversion

    /// <summary>
    /// Retrieves ECC information for the specified version and error correction level.
    /// </summary>
    /// <param name="version"></param>
    /// <param name="eccLevel"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentException"></exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ECCInfo GetEccInfo(int version, QREccLevel eccLevel)
    {
        var table = CapacityECCTable;
        for (var i = 0; i < table.Count; i++)
        {
            var item = table[i];
            if (item.Version == version && item.ErrorCorrectionLevel == eccLevel)
                return item;
        }

        throw new ArgumentException($"ECC info not found for version {version}, level {eccLevel}");
    }

    // Format and Version String Generation

    /// <summary>
    /// Generates 15-bit format information bits using BCH(15,5) error correction.
    /// Contains ECC level and mask pattern with error correction.
    /// </summary>
    /// <param name="level">Error correction level</param>
    /// <param name="maskVersion">Mask pattern version (0-7)</param>
    /// <returns>15-bit format information:
    /// <code>
    /// Bit layout: [ECC(2) | Mask(3) | BCH(10)] XOR 0b101010000010010
    /// - Bits 14-13: ECC level (L=01, M=00, Q=11, H=10)
    /// - Bits 12-10: Mask pattern (0-7)
    /// - Bits 9-0:   BCH error correction
    /// </code>
    /// </returns>
    /// <remarks>
    /// Based on ISO/IEC 18004 Section 7.8.2
    /// </remarks>
    public static ushort GetFormatBits(QREccLevel level, int maskVersion)
    {
        // BCH(15,5) error correction
        // BCH generator polynomial: x^10 + x^8 + x^5 + x^4 + x^2 + x + 1 (0b10100110111)
        const ushort BCH_GENERATOR_POLYNOMIAL = 0b10100110111;
        // Format information mask pattern (ISO/IEC 18004 Section 7.8.2)
        const ushort FORMAT_MASK_PATTERN = 0b101010000010010;

        // const int ECC_LEVEL_BITS = 2; // not use directly
        const int MASK_PATTERN_BITS = 3;
        // const int DATA_BITS = ECC_LEVEL_BITS + MASK_PATTERN_BITS; // not use directly
        const int ECC_CORRECTION_BITS = 10;
        const int TOTAL_BITS = 15;

        // ECC level bits (2bit)
        var formatBits = level switch
        {
            QREccLevel.L => 0b01,
            QREccLevel.M => 0b00,
            QREccLevel.Q => 0b11,
            QREccLevel.H => 0b10,
            _ => throw new ArgumentOutOfRangeException(nameof(level), level, "QREccLevel was out of range"),
        };

        // Add mask pattern bits (3bits)
        formatBits = (ushort)(formatBits << MASK_PATTERN_BITS | maskVersion);

        // Calculate BCH(15,5) error correction
        var temp = (ushort)(formatBits << ECC_CORRECTION_BITS);
        for (var i = TOTAL_BITS - 1; i >= ECC_CORRECTION_BITS; i--)
        {
            if ((temp & (1 << i)) != 0)
            {
                temp ^= (ushort)(BCH_GENERATOR_POLYNOMIAL << (i - ECC_CORRECTION_BITS));
            }
        }

        // Combine data and ECC bits, then apply mask (XOR)
        return (ushort)(((formatBits << ECC_CORRECTION_BITS) | temp) ^ FORMAT_MASK_PATTERN);
    }

    /// <summary>
    /// Generates 18-bit version information bits (for version 7+) using BCH(18,6) error correction.
    /// Contains version number with error correction.
    /// </summary>
    /// <param name="version">QR code version (7-40).</param>
    /// <returns>18-bit version information:
    /// <code>
    /// Bit layout: [Version(6) | BCH(12)]
    /// - Bits 17-12: Version number (7-40)
    /// - Bits 11-0:  BCH error correction
    /// </code>
    /// </returns>
    /// <remarks>
    /// Based on ISO/IEC 18004 Section 7.10
    /// </remarks>
    public static uint GetVersionBits(int version)
    {
        // BCH(18,6) error correction
        // BCH generator polynomial: x^12 + x^11 + x^10 + x^9 + x^8 + x^5 + x^2 + 1 (0b1111100100101)
        const uint BCH_GENERATOR_POLYNOMIAL = 0b1111100100101;

        // const int VERSION_DATA_BITS = 6; // not use directly
        const int ECC_CORRECTION_BITS = 12;
        const int TOTAL_BITS = 18;

        var vBits = (uint)version;
        var temp = vBits << ECC_CORRECTION_BITS;

        for (var i = TOTAL_BITS - 1; i >= ECC_CORRECTION_BITS; i--)
        {
            if ((temp & (1u << i)) != 0)
            {
                temp ^= (BCH_GENERATOR_POLYNOMIAL << (i - ECC_CORRECTION_BITS));
            }
        }

        return (vBits << ECC_CORRECTION_BITS) | temp;
    }

    // Lookup Table Initialization

    /// <summary>
    /// Initializes alignment pattern lookup table from base values.
    /// Computes actual (x, y) coordinates for each version's alignment patterns.
    /// </summary>
    private static IReadOnlyList<AlignmentPattern> CreateAlignmentPatternTable()
    {
        var table = new List<AlignmentPattern>(40); // 40 versions
        for (var i = 0; i < (7 * 40); i = i + 7)
        {
            var version = (i + 7) / 7;
            var capacity = CalculateAlignmentPatternCount(version);
            var points = new List<Point>(capacity);

            for (var x = 0; x < 7; x++)
            {
                if (AlignmentPatternBaseValues[i + x] != 0)
                {
                    for (var y = 0; y < 7; y++)
                    {
                        if (AlignmentPatternBaseValues[i + y] != 0)
                        {
                            var p = new Point(AlignmentPatternBaseValues[i + x] - 2, AlignmentPatternBaseValues[i + y] - 2);
                            if (!points.Contains(p))
                            {
                                points.Add(p);
                            }
                        }
                    }
                }
            }

            table.Add(new AlignmentPattern()
            {
                Version = (i + 7) / 7,
                PatternPositions = points
            });
        }

        return table;

        static int CalculateAlignmentPatternCount(int version)
        {
            // Version 1: 0 pattern
            // Version 2-6: 1 pattern (corner excluded
            // Version 7+: max 7x7 = 49 patterns (most cases less)
            if (version == 1) return 0;
            if (version <= 6) return 7; // Conservative estimate

            // Version 7+: Approximate based on grid
            var gridSize = (version / 7) + 2;
            return gridSize * gridSize; // Upper bound
        }
    }

    /// <summary>
    /// Initializes error correction configuration table from base values.
    /// Creates ECCInfo entries for all version/ECC level combinations.
    /// </summary>
    private static IReadOnlyList<ECCInfo> CreateCapacityECCTable()
    {
        var table = new List<ECCInfo>(160); // 40 versions × 4 ECC levels
        for (var i = 0; i < (4 * 6 * 40); i = i + (4 * 6))
        {
            table.AddRange([
                new ECCInfo(
                    version: (i+24) / 24,
                    errorCorrectionLevel: QREccLevel.L,
                    totalDataCodewords: CapacityECCBaseValues[i],
                    eccPerBlock: CapacityECCBaseValues[i+1],
                    blocksInGroup1: CapacityECCBaseValues[i+2],
                    codewordsInGroup1: CapacityECCBaseValues[i+3],
                    blocksInGroup2: CapacityECCBaseValues[i+4],
                    codewordsInGroup2: CapacityECCBaseValues[i+5]),
                new ECCInfo
                (
                    version: (i + 24) / 24,
                    errorCorrectionLevel: QREccLevel.M,
                    totalDataCodewords: CapacityECCBaseValues[i+6],
                    eccPerBlock: CapacityECCBaseValues[i+7],
                    blocksInGroup1: CapacityECCBaseValues[i+8],
                    codewordsInGroup1: CapacityECCBaseValues[i+9],
                    blocksInGroup2: CapacityECCBaseValues[i+10],
                    codewordsInGroup2: CapacityECCBaseValues[i+11]
                ),
                new ECCInfo
                (
                    version: (i + 24) / 24,
                    errorCorrectionLevel: QREccLevel.Q,
                    totalDataCodewords: CapacityECCBaseValues[i+12],
                    eccPerBlock: CapacityECCBaseValues[i+13],
                    blocksInGroup1: CapacityECCBaseValues[i+14],
                    codewordsInGroup1: CapacityECCBaseValues[i+15],
                    blocksInGroup2: CapacityECCBaseValues[i+16],
                    codewordsInGroup2: CapacityECCBaseValues[i+17]
                ),
                new ECCInfo
                (
                    version: (i + 24) / 24,
                    errorCorrectionLevel: QREccLevel.H,
                    totalDataCodewords: CapacityECCBaseValues[i+18],
                    eccPerBlock: CapacityECCBaseValues[i+19],
                    blocksInGroup1: CapacityECCBaseValues[i+20],
                    codewordsInGroup1: CapacityECCBaseValues[i+21],
                    blocksInGroup2: CapacityECCBaseValues[i+22],
                    codewordsInGroup2: CapacityECCBaseValues[i+23]
                )
            ]);
        }
        return table;
    }

    /// <summary>
    /// Initializes capacity lookup table from base values.
    /// Creates VersionInfo entries mapping version/ECC/mode to max capacity.
    /// </summary>
    private static IReadOnlyList<VersionInfo> CreateCapacityTable()
    {
        var table = new List<VersionInfo>(40); // 40 versions
        for (var i = 0; i < (16 * 40); i = i + 16)
        {
            table.Add(new VersionInfo(
                (i + 16) / 16,
                [
                    new VersionInfoDetails(
                            QREccLevel.L,
                            new (){
                                { EncodingMode.Numeric, CapacityBaseValues[i] },
                                { EncodingMode.Alphanumeric, CapacityBaseValues[i+1] },
                                { EncodingMode.Byte, CapacityBaseValues[i+2] },
                        }
                    ),
                    new VersionInfoDetails(
                            QREccLevel.M,
                            new (){
                                { EncodingMode.Numeric, CapacityBaseValues[i+4] },
                                { EncodingMode.Alphanumeric, CapacityBaseValues[i+5] },
                                { EncodingMode.Byte, CapacityBaseValues[i+6] },
                            }
                    ),
                    new VersionInfoDetails(
                            QREccLevel.Q,
                            new (){
                                { EncodingMode.Numeric, CapacityBaseValues[i+8] },
                                { EncodingMode.Alphanumeric, CapacityBaseValues[i+9] },
                                { EncodingMode.Byte, CapacityBaseValues[i+10] },
                            }
                    ),
                    new VersionInfoDetails(
                            QREccLevel.H,
                            new (){
                                { EncodingMode.Numeric, CapacityBaseValues[i+12] },
                                { EncodingMode.Alphanumeric, CapacityBaseValues[i+13] },
                                { EncodingMode.Byte, CapacityBaseValues[i+14] },
                            }
                    )
                ]
            ));
        }

        return table;
    }

    // struct definitions

    /// <summary>
    /// Alignment pattern information for a specific QR code version.
    /// </summary>
    public struct AlignmentPattern
    {
        public int Version;
        public List<Point> PatternPositions;
    }

    /// <summary>
    /// Version information container for QR code capacity data.
    /// Contains all encoding mode capacities for each error correction level.
    /// </summary>
    public struct VersionInfo
    {
        public VersionInfo(int version, List<VersionInfoDetails> versionInfoDetails)
        {
            Version = version;
            Details = versionInfoDetails;
        }

        /// <summary>QR code version (1-40).</summary>
        public int Version { get; }

        /// <summary>Capacity details for each error correction level (L, M, Q, H).</summary>
        public List<VersionInfoDetails> Details { get; }
    }

    /// <summary>
    /// Capacity information for a specific error correction level.
    /// Maps encoding modes to their maximum character/byte capacity.
    /// </summary>
    public struct VersionInfoDetails
    {
        public VersionInfoDetails(QREccLevel errorCorrectionLevel, Dictionary<EncodingMode, int> capacityDict)
        {
            ErrorCorrectionLevel = errorCorrectionLevel;
            CapacityDict = capacityDict;
        }

        /// <summary>Error correction level.</summary>
        public QREccLevel ErrorCorrectionLevel { get; }

        /// <summary>
        /// Maximum capacity for each encoding mode.
        /// Key: EncodingMode, Value: Maximum number of characters/bytes.
        /// </summary>
        public Dictionary<EncodingMode, int> CapacityDict { get; }
    }
}
