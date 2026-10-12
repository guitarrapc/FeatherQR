/*
 * GS1 Barcode Syntax Dictionary. Copyright (c) 2024 GS1 AISBL.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 *
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 *
 */

/**
 * @file lint_packagetype.c
 *
 * @brief The `packagetype` linter ensures that the data represents a valid
 * UN/ECE Recommendation 21 alphanumeric code, extended with additional GS1
 * code values.
 *
 * @remark The set of valid codes is maintained by GS1 in the [PackageTypeCode
 * code list](https://navigator.gs1.org/edi/codelist-details?name=PackageTypeCode).
 *
 */


#include <assert.h>
#include <string.h>

#include "gs1syntaxdictionary.h"
#include "gs1syntaxdictionary-utils.h"


/*
 * Include a header containing a replacement lookup function, if we are told
 * to.
 *
 */
#ifdef GS1_LINTER_CUSTOM_PACKAGE_TYPE_LOOKUP_H
#define xstr(s) str(s)
#define str(s) #s
#include xstr(GS1_LINTER_CUSTOM_PACKAGE_TYPE_LOOKUP_H)
#endif


/**
 * Used to validate that an AI component is a valid package type as defined by
 * the PackageTypeCode code list.
 *
 * @note The default lookup function provided by this linter is a binary search
 *       over a static list this is maintained in this file.
 * @note To enable this linter to hook into an alternative PackageTypeCode
 *       lookup function (provided by the user) the
 *       GS1_LINTER_CUSTOM_PACKAGE_TYPE_LOOKUP_H macro may be set to the name of a
 *       header file to be included that defines a custom
 *       `GS1_LINTER_CUSTOM_PACKAGE_TYPE_LOOKUP` macro.
 * @note If provided, the GS1_LINTER_CUSTOM_PACKAGE_TYPE_LOOKUP macro shall invoke
 *       whatever functionality is available in the user-provided lookup
 *       function using the first and second arguments, then using the result must assign
 *       to third (output) argument as follows:
 *         - `valid`: Set to 1 if the lookup was successful. Otherwise 0.
 *
 * @param [in] data Pointer to the data to be linted. Must not be `NULL`.
 * @param [in] data_len Length of the data to be linted.
 * @param [out] err_pos To facilitate error highlighting, the start position of
 *                      the bad data is written to this pointer, if not `NULL`.
 * @param [out] err_len The length of the bad data is written to this pointer, if
 *                      not `NULL`.
 *
 * @return #GS1_LINTER_OK if okay.
 * @return #GS1_LINTER_INVALID_PACKAGE_TYPE if the data is not a valid PackageTypeCode.
 *
 */
GS1_SYNTAX_DICTIONARY_API gs1_lint_err_t gs1_lint_packagetype(const char* const data, size_t data_len, size_t* const err_pos, size_t* const err_len)
{

	/*
	 * Allow for a custom replacement of the lookup code to be provided.
	 *
	 */
#ifdef GS1_LINTER_CUSTOM_PACKAGE_TYPE_LOOKUP
#define GS1_LINTER_PACKAGE_TYPE_LOOKUP(cc, cc_len, valid) GS1_LINTER_CUSTOM_PACKAGE_TYPE_LOOKUP(cc, cc_len, valid)
#else

	/*
	 *  Set of valid PackageTypeCode value, in lexicographic order
	 *
	 *  MAINTENANCE NOTE:
	 *
	 *  Updates must be aligned with the PackageTypeCode code list
	 *
	 */
	static const char packagetypes[][4] = {
		"1A", "1B", "1D", "1F", "1G", "1W",
		"200", "201", "202", "203", "204", "205", "206", "210", "211", "212", "2C",
		"3A", "3H",
		"43", "44", "4A", "4B", "4C", "4D", "4F", "4G", "4H",
		"5H", "5L", "5M",
		"6H", "6P",
		"7A", "7B",
		"8", "8A", "8B", "8C",
		"9",
		"AA", "AB", "AC", "AD", "AF", "AG", "AH", "AI", "AJ", "AL", "AM", "AP", "APE","AT", "AV",
		"B4", "BB", "BC", "BD", "BE", "BF", "BG", "BGE", "BH", "BI", "BJ", "BK", "BL", "BM", "BME", "BN", "BO", "BP", "BQ", "BR", "BRI", "BS", "BT", "BU", "BV", "BW", "BX", "BY", "BZ",
		"CA", "CB", "CBL", "CC", "CCE", "CD", "CE", "CF", "CG", "CH", "CI", "CJ", "CK", "CL", "CM", "CN", "CO", "CP", "CQ", "CR", "CS", "CT", "CU", "CV", "CW", "CX", "CY", "CZ",
		"DA", "DB", "DC", "DG", "DH", "DI", "DJ", "DK", "DL", "DM", "DN", "DP", "DPE", "DR", "DS", "DT", "DU", "DV", "DW", "DX", "DY",
		"E1", "E2", "E3", "EC", "ED", "EE", "EF", "EG", "EH", "EI", "EN",
		"FB", "FC", "FD", "FE", "FI", "FL", "FO", "FOB", "FP", "FPE", "FR", "FT", "FW", "FX",
		"GB", "GI", "GL", "GR", "GU", "GY", "GZ",
		"HA", "HB", "HC", "HG", "HN", "HR",
		"IA", "IB", "IC", "ID", "IE", "IF", "IG", "IH", "IK", "IL", "IN", "IZ",
		"JB", "JC", "JG", "JR", "JT", "JY",
		"KG", "KI",
		"LAB", "LE", "LG", "LT", "LU", "LV", "LZ",
		"MA", "MB", "MC", "ME", "MPE", "MR", "MS", "MT", "MW", "MX",
		"NA", "NE", "NF", "NG", "NS", "NT", "NU", "NV",
		"OA", "OB", "OC", "OD", "OE", "OF", "OK", "OPE", "OT", "OU",
		"P2", "PA", "PAE", "PB", "PC", "PD", "PE", "PF", "PG", "PH", "PI", "PJ", "PK", "PL", "PLP", "PN", "PO", "POP", "PP", "PPE", "PR", "PT", "PU", "PUE", "PV", "PX", "PY", "PZ",
		"QA", "QB", "QC", "QD", "QF", "QG", "QH", "QJ", "QK", "QL", "QM", "QN", "QP", "QQ", "QR", "QS",
		"RB1", "RB2", "RB3", "RCB", "RD", "RG", "RJ", "RK", "RL", "RO", "RT", "RZ",
		"S1", "SA", "SB", "SC", "SD", "SE", "SEC", "SH", "SI", "SK", "SL", "SM", "SO", "SP", "SS", "ST", "STL", "SU", "SV", "SW", "SX", "SY", "SZ",
		"T1", "TB", "TC", "TD", "TE", "TEV", "TG", "THE", "TI", "TK", "TL", "TN", "TO", "TR", "TRE", "TS", "TT", "TTE", "TU", "TV", "TW", "TWE", "TY", "TZ",
		"UC", "UN", "UUE",
		"VA", "VG", "VI", "VK", "VL", "VN", "VO", "VP", "VQ", "VR", "VS", "VY",
		"WA", "WB", "WC", "WD", "WF", "WG", "WH", "WJ", "WK", "WL", "WM", "WN", "WP", "WQ", "WR", "WRP", "WS", "WT", "WU", "WV", "WW", "WX", "WY", "WZ",
		"X11", "X12", "X15", "X16", "X17", "X18", "X19", "X20", "X3", "XA", "XB", "XC", "XD", "XF", "XG", "XH", "XJ", "XK",
		"YA", "YB", "YC", "YD", "YF", "YG", "YH", "YJ", "YK", "YL", "YM", "YN", "YP", "YQ", "YR", "YS", "YT", "YV", "YW", "YX", "YY", "YZ",
		"ZA", "ZB", "ZC", "ZD", "ZF", "ZG", "ZH", "ZJ", "ZK", "ZL", "ZM", "ZN", "ZP", "ZQ", "ZR", "ZS", "ZT", "ZU", "ZV", "ZW", "ZX", "ZY", "ZZ",
	};

/// \cond
#define GS1_LINTER_PACKAGE_TYPE_LOOKUP(cc, cc_len, valid) GS1_LINTER_BINARY_SEARCH(cc, cc_len, packagetypes, valid)
/// \endcond

#endif

	int valid;

	assert(data);


	/*
	 * Ensure that the data is in the list.
	 *
	 */
	GS1_LINTER_PACKAGE_TYPE_LOOKUP(data, data_len, valid);
	if (GS1_LINTER_LIKELY(valid))
		GS1_LINTER_RETURN_OK;

	/*
	 * If not valid then indicate an error.
	 *
	 */
	GS1_LINTER_RETURN_ERROR(
		GS1_LINTER_INVALID_PACKAGE_TYPE,
		0,
		data_len
	);

}


#ifdef UNIT_TESTS

#include "unittest.h"

void test_lint_packagetype(void)
{

	UNIT_TEST_FAIL(gs1_lint_packagetype, "", GS1_LINTER_INVALID_PACKAGE_TYPE, "**");
	UNIT_TEST_FAIL(gs1_lint_packagetype, "0", GS1_LINTER_INVALID_PACKAGE_TYPE, "*0*");
	UNIT_TEST_FAIL(gs1_lint_packagetype, "00", GS1_LINTER_INVALID_PACKAGE_TYPE, "*00*");
	UNIT_TEST_FAIL(gs1_lint_packagetype, "000", GS1_LINTER_INVALID_PACKAGE_TYPE, "*000*");
	UNIT_TEST_FAIL(gs1_lint_packagetype, "0000", GS1_LINTER_INVALID_PACKAGE_TYPE, "*0000*");
	UNIT_TEST_FAIL(gs1_lint_packagetype, "_9", GS1_LINTER_INVALID_PACKAGE_TYPE, "*_9*");
	UNIT_TEST_FAIL(gs1_lint_packagetype, "9_", GS1_LINTER_INVALID_PACKAGE_TYPE, "*9_*");
	UNIT_TEST_FAIL(gs1_lint_packagetype, "_1A", GS1_LINTER_INVALID_PACKAGE_TYPE, "*_1A*");
	UNIT_TEST_FAIL(gs1_lint_packagetype, "1A_", GS1_LINTER_INVALID_PACKAGE_TYPE, "*1A_*");
	UNIT_TEST_FAIL(gs1_lint_packagetype, "_200", GS1_LINTER_INVALID_PACKAGE_TYPE, "*_200*");
	UNIT_TEST_FAIL(gs1_lint_packagetype, "200_", GS1_LINTER_INVALID_PACKAGE_TYPE, "*200_*");

	UNIT_TEST_PASS(gs1_lint_packagetype, "1A");
	UNIT_TEST_PASS(gs1_lint_packagetype, "1B");
	UNIT_TEST_PASS(gs1_lint_packagetype, "1D");
	UNIT_TEST_PASS(gs1_lint_packagetype, "1F");
	UNIT_TEST_PASS(gs1_lint_packagetype, "1G");
	UNIT_TEST_PASS(gs1_lint_packagetype, "1W");
	UNIT_TEST_PASS(gs1_lint_packagetype, "200");
	UNIT_TEST_PASS(gs1_lint_packagetype, "201");
	UNIT_TEST_PASS(gs1_lint_packagetype, "202");
	UNIT_TEST_PASS(gs1_lint_packagetype, "203");
	UNIT_TEST_PASS(gs1_lint_packagetype, "204");
	UNIT_TEST_PASS(gs1_lint_packagetype, "205");
	UNIT_TEST_PASS(gs1_lint_packagetype, "206");
	UNIT_TEST_PASS(gs1_lint_packagetype, "210");
	UNIT_TEST_PASS(gs1_lint_packagetype, "211");
	UNIT_TEST_PASS(gs1_lint_packagetype, "212");
	UNIT_TEST_PASS(gs1_lint_packagetype, "2C");
	UNIT_TEST_PASS(gs1_lint_packagetype, "3A");
	UNIT_TEST_PASS(gs1_lint_packagetype, "3H");
	UNIT_TEST_PASS(gs1_lint_packagetype, "43");
	UNIT_TEST_PASS(gs1_lint_packagetype, "44");
	UNIT_TEST_PASS(gs1_lint_packagetype, "4A");
	UNIT_TEST_PASS(gs1_lint_packagetype, "4B");
	UNIT_TEST_PASS(gs1_lint_packagetype, "4C");
	UNIT_TEST_PASS(gs1_lint_packagetype, "4D");
	UNIT_TEST_PASS(gs1_lint_packagetype, "4F");
	UNIT_TEST_PASS(gs1_lint_packagetype, "4G");
	UNIT_TEST_PASS(gs1_lint_packagetype, "4H");
	UNIT_TEST_PASS(gs1_lint_packagetype, "5H");
	UNIT_TEST_PASS(gs1_lint_packagetype, "5L");
	UNIT_TEST_PASS(gs1_lint_packagetype, "5M");
	UNIT_TEST_PASS(gs1_lint_packagetype, "6H");
	UNIT_TEST_PASS(gs1_lint_packagetype, "6P");
	UNIT_TEST_PASS(gs1_lint_packagetype, "7A");
	UNIT_TEST_PASS(gs1_lint_packagetype, "7B");
	UNIT_TEST_PASS(gs1_lint_packagetype, "8");
	UNIT_TEST_PASS(gs1_lint_packagetype, "8A");
	UNIT_TEST_PASS(gs1_lint_packagetype, "8B");
	UNIT_TEST_PASS(gs1_lint_packagetype, "8C");
	UNIT_TEST_PASS(gs1_lint_packagetype, "9");
	UNIT_TEST_PASS(gs1_lint_packagetype, "AA");
	UNIT_TEST_PASS(gs1_lint_packagetype, "AB");
	UNIT_TEST_PASS(gs1_lint_packagetype, "AC");
	UNIT_TEST_PASS(gs1_lint_packagetype, "AD");
	UNIT_TEST_PASS(gs1_lint_packagetype, "AF");
	UNIT_TEST_PASS(gs1_lint_packagetype, "AG");
	UNIT_TEST_PASS(gs1_lint_packagetype, "AH");
	UNIT_TEST_PASS(gs1_lint_packagetype, "AI");
	UNIT_TEST_PASS(gs1_lint_packagetype, "AJ");
	UNIT_TEST_PASS(gs1_lint_packagetype, "AL");
	UNIT_TEST_PASS(gs1_lint_packagetype, "AM");
	UNIT_TEST_PASS(gs1_lint_packagetype, "AP");
	UNIT_TEST_PASS(gs1_lint_packagetype, "APE");
	UNIT_TEST_PASS(gs1_lint_packagetype, "AT");
	UNIT_TEST_PASS(gs1_lint_packagetype, "AV");
	UNIT_TEST_PASS(gs1_lint_packagetype, "B4");
	UNIT_TEST_PASS(gs1_lint_packagetype, "BB");
	UNIT_TEST_PASS(gs1_lint_packagetype, "BC");
	UNIT_TEST_PASS(gs1_lint_packagetype, "BD");
	UNIT_TEST_PASS(gs1_lint_packagetype, "BE");
	UNIT_TEST_PASS(gs1_lint_packagetype, "BF");
	UNIT_TEST_PASS(gs1_lint_packagetype, "BG");
	UNIT_TEST_PASS(gs1_lint_packagetype, "BGE");
	UNIT_TEST_PASS(gs1_lint_packagetype, "BH");
	UNIT_TEST_PASS(gs1_lint_packagetype, "BI");
	UNIT_TEST_PASS(gs1_lint_packagetype, "BJ");
	UNIT_TEST_PASS(gs1_lint_packagetype, "BK");
	UNIT_TEST_PASS(gs1_lint_packagetype, "BL");
	UNIT_TEST_PASS(gs1_lint_packagetype, "BM");
	UNIT_TEST_PASS(gs1_lint_packagetype, "BME");
	UNIT_TEST_PASS(gs1_lint_packagetype, "BN");
	UNIT_TEST_PASS(gs1_lint_packagetype, "BO");
	UNIT_TEST_PASS(gs1_lint_packagetype, "BP");
	UNIT_TEST_PASS(gs1_lint_packagetype, "BQ");
	UNIT_TEST_PASS(gs1_lint_packagetype, "BR");
	UNIT_TEST_PASS(gs1_lint_packagetype, "BRI");
	UNIT_TEST_PASS(gs1_lint_packagetype, "BS");
	UNIT_TEST_PASS(gs1_lint_packagetype, "BT");
	UNIT_TEST_PASS(gs1_lint_packagetype, "BU");
	UNIT_TEST_PASS(gs1_lint_packagetype, "BV");
	UNIT_TEST_PASS(gs1_lint_packagetype, "BW");
	UNIT_TEST_PASS(gs1_lint_packagetype, "BX");
	UNIT_TEST_PASS(gs1_lint_packagetype, "BY");
	UNIT_TEST_PASS(gs1_lint_packagetype, "BZ");
	UNIT_TEST_PASS(gs1_lint_packagetype, "CA");
	UNIT_TEST_PASS(gs1_lint_packagetype, "CB");
	UNIT_TEST_PASS(gs1_lint_packagetype, "CBL");
	UNIT_TEST_PASS(gs1_lint_packagetype, "CC");
	UNIT_TEST_PASS(gs1_lint_packagetype, "CCE");
	UNIT_TEST_PASS(gs1_lint_packagetype, "CD");
	UNIT_TEST_PASS(gs1_lint_packagetype, "CE");
	UNIT_TEST_PASS(gs1_lint_packagetype, "CF");
	UNIT_TEST_PASS(gs1_lint_packagetype, "CG");
	UNIT_TEST_PASS(gs1_lint_packagetype, "CH");
	UNIT_TEST_PASS(gs1_lint_packagetype, "CI");
	UNIT_TEST_PASS(gs1_lint_packagetype, "CJ");
	UNIT_TEST_PASS(gs1_lint_packagetype, "CK");
	UNIT_TEST_PASS(gs1_lint_packagetype, "CL");
	UNIT_TEST_PASS(gs1_lint_packagetype, "CM");
	UNIT_TEST_PASS(gs1_lint_packagetype, "CN");
	UNIT_TEST_PASS(gs1_lint_packagetype, "CO");
	UNIT_TEST_PASS(gs1_lint_packagetype, "CP");
	UNIT_TEST_PASS(gs1_lint_packagetype, "CQ");
	UNIT_TEST_PASS(gs1_lint_packagetype, "CR");
	UNIT_TEST_PASS(gs1_lint_packagetype, "CS");
	UNIT_TEST_PASS(gs1_lint_packagetype, "CT");
	UNIT_TEST_PASS(gs1_lint_packagetype, "CU");
	UNIT_TEST_PASS(gs1_lint_packagetype, "CV");
	UNIT_TEST_PASS(gs1_lint_packagetype, "CW");
	UNIT_TEST_PASS(gs1_lint_packagetype, "CX");
	UNIT_TEST_PASS(gs1_lint_packagetype, "CY");
	UNIT_TEST_PASS(gs1_lint_packagetype, "CZ");
	UNIT_TEST_PASS(gs1_lint_packagetype, "DA");
	UNIT_TEST_PASS(gs1_lint_packagetype, "DB");
	UNIT_TEST_PASS(gs1_lint_packagetype, "DC");
	UNIT_TEST_PASS(gs1_lint_packagetype, "DG");
	UNIT_TEST_PASS(gs1_lint_packagetype, "DH");
	UNIT_TEST_PASS(gs1_lint_packagetype, "DI");
	UNIT_TEST_PASS(gs1_lint_packagetype, "DJ");
	UNIT_TEST_PASS(gs1_lint_packagetype, "DK");
	UNIT_TEST_PASS(gs1_lint_packagetype, "DL");
	UNIT_TEST_PASS(gs1_lint_packagetype, "DM");
	UNIT_TEST_PASS(gs1_lint_packagetype, "DN");
	UNIT_TEST_PASS(gs1_lint_packagetype, "DP");
	UNIT_TEST_PASS(gs1_lint_packagetype, "DPE");
	UNIT_TEST_PASS(gs1_lint_packagetype, "DR");
	UNIT_TEST_PASS(gs1_lint_packagetype, "DS");
	UNIT_TEST_PASS(gs1_lint_packagetype, "DT");
	UNIT_TEST_PASS(gs1_lint_packagetype, "DU");
	UNIT_TEST_PASS(gs1_lint_packagetype, "DV");
	UNIT_TEST_PASS(gs1_lint_packagetype, "DW");
	UNIT_TEST_PASS(gs1_lint_packagetype, "DX");
	UNIT_TEST_PASS(gs1_lint_packagetype, "DY");
	UNIT_TEST_PASS(gs1_lint_packagetype, "E1");
	UNIT_TEST_PASS(gs1_lint_packagetype, "E2");
	UNIT_TEST_PASS(gs1_lint_packagetype, "E3");
	UNIT_TEST_PASS(gs1_lint_packagetype, "EC");
	UNIT_TEST_PASS(gs1_lint_packagetype, "ED");
	UNIT_TEST_PASS(gs1_lint_packagetype, "EE");
	UNIT_TEST_PASS(gs1_lint_packagetype, "EF");
	UNIT_TEST_PASS(gs1_lint_packagetype, "EG");
	UNIT_TEST_PASS(gs1_lint_packagetype, "EH");
	UNIT_TEST_PASS(gs1_lint_packagetype, "EI");
	UNIT_TEST_PASS(gs1_lint_packagetype, "EN");
	UNIT_TEST_PASS(gs1_lint_packagetype, "FB");
	UNIT_TEST_PASS(gs1_lint_packagetype, "FC");
	UNIT_TEST_PASS(gs1_lint_packagetype, "FD");
	UNIT_TEST_PASS(gs1_lint_packagetype, "FE");
	UNIT_TEST_PASS(gs1_lint_packagetype, "FI");
	UNIT_TEST_PASS(gs1_lint_packagetype, "FL");
	UNIT_TEST_PASS(gs1_lint_packagetype, "FO");
	UNIT_TEST_PASS(gs1_lint_packagetype, "FOB");
	UNIT_TEST_PASS(gs1_lint_packagetype, "FP");
	UNIT_TEST_PASS(gs1_lint_packagetype, "FPE");
	UNIT_TEST_PASS(gs1_lint_packagetype, "FR");
	UNIT_TEST_PASS(gs1_lint_packagetype, "FT");
	UNIT_TEST_PASS(gs1_lint_packagetype, "FW");
	UNIT_TEST_PASS(gs1_lint_packagetype, "FX");
	UNIT_TEST_PASS(gs1_lint_packagetype, "GB");
	UNIT_TEST_PASS(gs1_lint_packagetype, "GI");
	UNIT_TEST_PASS(gs1_lint_packagetype, "GL");
	UNIT_TEST_PASS(gs1_lint_packagetype, "GR");
	UNIT_TEST_PASS(gs1_lint_packagetype, "GU");
	UNIT_TEST_PASS(gs1_lint_packagetype, "GY");
	UNIT_TEST_PASS(gs1_lint_packagetype, "GZ");
	UNIT_TEST_PASS(gs1_lint_packagetype, "HA");
	UNIT_TEST_PASS(gs1_lint_packagetype, "HB");
	UNIT_TEST_PASS(gs1_lint_packagetype, "HC");
	UNIT_TEST_PASS(gs1_lint_packagetype, "HG");
	UNIT_TEST_PASS(gs1_lint_packagetype, "HN");
	UNIT_TEST_PASS(gs1_lint_packagetype, "HR");
	UNIT_TEST_PASS(gs1_lint_packagetype, "IA");
	UNIT_TEST_PASS(gs1_lint_packagetype, "IB");
	UNIT_TEST_PASS(gs1_lint_packagetype, "IC");
	UNIT_TEST_PASS(gs1_lint_packagetype, "ID");
	UNIT_TEST_PASS(gs1_lint_packagetype, "IE");
	UNIT_TEST_PASS(gs1_lint_packagetype, "IF");
	UNIT_TEST_PASS(gs1_lint_packagetype, "IG");
	UNIT_TEST_PASS(gs1_lint_packagetype, "IH");
	UNIT_TEST_PASS(gs1_lint_packagetype, "IK");
	UNIT_TEST_PASS(gs1_lint_packagetype, "IL");
	UNIT_TEST_PASS(gs1_lint_packagetype, "IN");
	UNIT_TEST_PASS(gs1_lint_packagetype, "IZ");
	UNIT_TEST_PASS(gs1_lint_packagetype, "JB");
	UNIT_TEST_PASS(gs1_lint_packagetype, "JC");
	UNIT_TEST_PASS(gs1_lint_packagetype, "JG");
	UNIT_TEST_PASS(gs1_lint_packagetype, "JR");
	UNIT_TEST_PASS(gs1_lint_packagetype, "JT");
	UNIT_TEST_PASS(gs1_lint_packagetype, "JY");
	UNIT_TEST_PASS(gs1_lint_packagetype, "KG");
	UNIT_TEST_PASS(gs1_lint_packagetype, "KI");
	UNIT_TEST_PASS(gs1_lint_packagetype, "LAB");
	UNIT_TEST_PASS(gs1_lint_packagetype, "LE");
	UNIT_TEST_PASS(gs1_lint_packagetype, "LG");
	UNIT_TEST_PASS(gs1_lint_packagetype, "LT");
	UNIT_TEST_PASS(gs1_lint_packagetype, "LU");
	UNIT_TEST_PASS(gs1_lint_packagetype, "LV");
	UNIT_TEST_PASS(gs1_lint_packagetype, "LZ");
	UNIT_TEST_PASS(gs1_lint_packagetype, "MA");
	UNIT_TEST_PASS(gs1_lint_packagetype, "MB");
	UNIT_TEST_PASS(gs1_lint_packagetype, "MC");
	UNIT_TEST_PASS(gs1_lint_packagetype, "ME");
	UNIT_TEST_PASS(gs1_lint_packagetype, "MPE");
	UNIT_TEST_PASS(gs1_lint_packagetype, "MR");
	UNIT_TEST_PASS(gs1_lint_packagetype, "MS");
	UNIT_TEST_PASS(gs1_lint_packagetype, "MT");
	UNIT_TEST_PASS(gs1_lint_packagetype, "MW");
	UNIT_TEST_PASS(gs1_lint_packagetype, "MX");
	UNIT_TEST_PASS(gs1_lint_packagetype, "NA");
	UNIT_TEST_PASS(gs1_lint_packagetype, "NE");
	UNIT_TEST_PASS(gs1_lint_packagetype, "NF");
	UNIT_TEST_PASS(gs1_lint_packagetype, "NG");
	UNIT_TEST_PASS(gs1_lint_packagetype, "NS");
	UNIT_TEST_PASS(gs1_lint_packagetype, "NT");
	UNIT_TEST_PASS(gs1_lint_packagetype, "NU");
	UNIT_TEST_PASS(gs1_lint_packagetype, "NV");
	UNIT_TEST_PASS(gs1_lint_packagetype, "OA");
	UNIT_TEST_PASS(gs1_lint_packagetype, "OB");
	UNIT_TEST_PASS(gs1_lint_packagetype, "OC");
	UNIT_TEST_PASS(gs1_lint_packagetype, "OD");
	UNIT_TEST_PASS(gs1_lint_packagetype, "OE");
	UNIT_TEST_PASS(gs1_lint_packagetype, "OF");
	UNIT_TEST_PASS(gs1_lint_packagetype, "OK");
	UNIT_TEST_PASS(gs1_lint_packagetype, "OPE");
	UNIT_TEST_PASS(gs1_lint_packagetype, "OT");
	UNIT_TEST_PASS(gs1_lint_packagetype, "OU");
	UNIT_TEST_PASS(gs1_lint_packagetype, "P2");
	UNIT_TEST_PASS(gs1_lint_packagetype, "PA");
	UNIT_TEST_PASS(gs1_lint_packagetype, "PAE");
	UNIT_TEST_PASS(gs1_lint_packagetype, "PB");
	UNIT_TEST_PASS(gs1_lint_packagetype, "PC");
	UNIT_TEST_PASS(gs1_lint_packagetype, "PD");
	UNIT_TEST_PASS(gs1_lint_packagetype, "PE");
	UNIT_TEST_PASS(gs1_lint_packagetype, "PF");
	UNIT_TEST_PASS(gs1_lint_packagetype, "PG");
	UNIT_TEST_PASS(gs1_lint_packagetype, "PH");
	UNIT_TEST_PASS(gs1_lint_packagetype, "PI");
	UNIT_TEST_PASS(gs1_lint_packagetype, "PJ");
	UNIT_TEST_PASS(gs1_lint_packagetype, "PK");
	UNIT_TEST_PASS(gs1_lint_packagetype, "PL");
	UNIT_TEST_PASS(gs1_lint_packagetype, "PLP");
	UNIT_TEST_PASS(gs1_lint_packagetype, "PN");
	UNIT_TEST_PASS(gs1_lint_packagetype, "PO");
	UNIT_TEST_PASS(gs1_lint_packagetype, "POP");
	UNIT_TEST_PASS(gs1_lint_packagetype, "PP");
	UNIT_TEST_PASS(gs1_lint_packagetype, "PPE");
	UNIT_TEST_PASS(gs1_lint_packagetype, "PR");
	UNIT_TEST_PASS(gs1_lint_packagetype, "PT");
	UNIT_TEST_PASS(gs1_lint_packagetype, "PU");
	UNIT_TEST_PASS(gs1_lint_packagetype, "PUE");
	UNIT_TEST_PASS(gs1_lint_packagetype, "PV");
	UNIT_TEST_PASS(gs1_lint_packagetype, "PX");
	UNIT_TEST_PASS(gs1_lint_packagetype, "PY");
	UNIT_TEST_PASS(gs1_lint_packagetype, "PZ");
	UNIT_TEST_PASS(gs1_lint_packagetype, "QA");
	UNIT_TEST_PASS(gs1_lint_packagetype, "QB");
	UNIT_TEST_PASS(gs1_lint_packagetype, "QC");
	UNIT_TEST_PASS(gs1_lint_packagetype, "QD");
	UNIT_TEST_PASS(gs1_lint_packagetype, "QF");
	UNIT_TEST_PASS(gs1_lint_packagetype, "QG");
	UNIT_TEST_PASS(gs1_lint_packagetype, "QH");
	UNIT_TEST_PASS(gs1_lint_packagetype, "QJ");
	UNIT_TEST_PASS(gs1_lint_packagetype, "QK");
	UNIT_TEST_PASS(gs1_lint_packagetype, "QL");
	UNIT_TEST_PASS(gs1_lint_packagetype, "QM");
	UNIT_TEST_PASS(gs1_lint_packagetype, "QN");
	UNIT_TEST_PASS(gs1_lint_packagetype, "QP");
	UNIT_TEST_PASS(gs1_lint_packagetype, "QQ");
	UNIT_TEST_PASS(gs1_lint_packagetype, "QR");
	UNIT_TEST_PASS(gs1_lint_packagetype, "QS");
	UNIT_TEST_PASS(gs1_lint_packagetype, "RB1");
	UNIT_TEST_PASS(gs1_lint_packagetype, "RB2");
	UNIT_TEST_PASS(gs1_lint_packagetype, "RB3");
	UNIT_TEST_PASS(gs1_lint_packagetype, "RCB");
	UNIT_TEST_PASS(gs1_lint_packagetype, "RD");
	UNIT_TEST_PASS(gs1_lint_packagetype, "RG");
	UNIT_TEST_PASS(gs1_lint_packagetype, "RJ");
	UNIT_TEST_PASS(gs1_lint_packagetype, "RK");
	UNIT_TEST_PASS(gs1_lint_packagetype, "RL");
	UNIT_TEST_PASS(gs1_lint_packagetype, "RO");
	UNIT_TEST_PASS(gs1_lint_packagetype, "RT");
	UNIT_TEST_PASS(gs1_lint_packagetype, "RZ");
	UNIT_TEST_PASS(gs1_lint_packagetype, "S1");
	UNIT_TEST_PASS(gs1_lint_packagetype, "SA");
	UNIT_TEST_PASS(gs1_lint_packagetype, "SB");
	UNIT_TEST_PASS(gs1_lint_packagetype, "SC");
	UNIT_TEST_PASS(gs1_lint_packagetype, "SD");
	UNIT_TEST_PASS(gs1_lint_packagetype, "SE");
	UNIT_TEST_PASS(gs1_lint_packagetype, "SEC");
	UNIT_TEST_PASS(gs1_lint_packagetype, "SH");
	UNIT_TEST_PASS(gs1_lint_packagetype, "SI");
	UNIT_TEST_PASS(gs1_lint_packagetype, "SK");
	UNIT_TEST_PASS(gs1_lint_packagetype, "SL");
	UNIT_TEST_PASS(gs1_lint_packagetype, "SM");
	UNIT_TEST_PASS(gs1_lint_packagetype, "SO");
	UNIT_TEST_PASS(gs1_lint_packagetype, "SP");
	UNIT_TEST_PASS(gs1_lint_packagetype, "SS");
	UNIT_TEST_PASS(gs1_lint_packagetype, "ST");
	UNIT_TEST_PASS(gs1_lint_packagetype, "STL");
	UNIT_TEST_PASS(gs1_lint_packagetype, "SU");
	UNIT_TEST_PASS(gs1_lint_packagetype, "SV");
	UNIT_TEST_PASS(gs1_lint_packagetype, "SW");
	UNIT_TEST_PASS(gs1_lint_packagetype, "SX");
	UNIT_TEST_PASS(gs1_lint_packagetype, "SY");
	UNIT_TEST_PASS(gs1_lint_packagetype, "SZ");
	UNIT_TEST_PASS(gs1_lint_packagetype, "T1");
	UNIT_TEST_PASS(gs1_lint_packagetype, "TB");
	UNIT_TEST_PASS(gs1_lint_packagetype, "TC");
	UNIT_TEST_PASS(gs1_lint_packagetype, "TD");
	UNIT_TEST_PASS(gs1_lint_packagetype, "TE");
	UNIT_TEST_PASS(gs1_lint_packagetype, "TEV");
	UNIT_TEST_PASS(gs1_lint_packagetype, "TG");
	UNIT_TEST_PASS(gs1_lint_packagetype, "THE");
	UNIT_TEST_PASS(gs1_lint_packagetype, "TI");
	UNIT_TEST_PASS(gs1_lint_packagetype, "TK");
	UNIT_TEST_PASS(gs1_lint_packagetype, "TL");
	UNIT_TEST_PASS(gs1_lint_packagetype, "TN");
	UNIT_TEST_PASS(gs1_lint_packagetype, "TO");
	UNIT_TEST_PASS(gs1_lint_packagetype, "TR");
	UNIT_TEST_PASS(gs1_lint_packagetype, "TRE");
	UNIT_TEST_PASS(gs1_lint_packagetype, "TS");
	UNIT_TEST_PASS(gs1_lint_packagetype, "TT");
	UNIT_TEST_PASS(gs1_lint_packagetype, "TTE");
	UNIT_TEST_PASS(gs1_lint_packagetype, "TU");
	UNIT_TEST_PASS(gs1_lint_packagetype, "TV");
	UNIT_TEST_PASS(gs1_lint_packagetype, "TW");
	UNIT_TEST_PASS(gs1_lint_packagetype, "TWE");
	UNIT_TEST_PASS(gs1_lint_packagetype, "TY");
	UNIT_TEST_PASS(gs1_lint_packagetype, "TZ");
	UNIT_TEST_PASS(gs1_lint_packagetype, "UC");
	UNIT_TEST_PASS(gs1_lint_packagetype, "UN");
	UNIT_TEST_PASS(gs1_lint_packagetype, "UUE");
	UNIT_TEST_PASS(gs1_lint_packagetype, "VA");
	UNIT_TEST_PASS(gs1_lint_packagetype, "VG");
	UNIT_TEST_PASS(gs1_lint_packagetype, "VI");
	UNIT_TEST_PASS(gs1_lint_packagetype, "VK");
	UNIT_TEST_PASS(gs1_lint_packagetype, "VL");
	UNIT_TEST_PASS(gs1_lint_packagetype, "VN");
	UNIT_TEST_PASS(gs1_lint_packagetype, "VO");
	UNIT_TEST_PASS(gs1_lint_packagetype, "VP");
	UNIT_TEST_PASS(gs1_lint_packagetype, "VQ");
	UNIT_TEST_PASS(gs1_lint_packagetype, "VR");
	UNIT_TEST_PASS(gs1_lint_packagetype, "VS");
	UNIT_TEST_PASS(gs1_lint_packagetype, "VY");
	UNIT_TEST_PASS(gs1_lint_packagetype, "WA");
	UNIT_TEST_PASS(gs1_lint_packagetype, "WB");
	UNIT_TEST_PASS(gs1_lint_packagetype, "WC");
	UNIT_TEST_PASS(gs1_lint_packagetype, "WD");
	UNIT_TEST_PASS(gs1_lint_packagetype, "WF");
	UNIT_TEST_PASS(gs1_lint_packagetype, "WG");
	UNIT_TEST_PASS(gs1_lint_packagetype, "WH");
	UNIT_TEST_PASS(gs1_lint_packagetype, "WJ");
	UNIT_TEST_PASS(gs1_lint_packagetype, "WK");
	UNIT_TEST_PASS(gs1_lint_packagetype, "WL");
	UNIT_TEST_PASS(gs1_lint_packagetype, "WM");
	UNIT_TEST_PASS(gs1_lint_packagetype, "WN");
	UNIT_TEST_PASS(gs1_lint_packagetype, "WP");
	UNIT_TEST_PASS(gs1_lint_packagetype, "WQ");
	UNIT_TEST_PASS(gs1_lint_packagetype, "WR");
	UNIT_TEST_PASS(gs1_lint_packagetype, "WRP");
	UNIT_TEST_PASS(gs1_lint_packagetype, "WS");
	UNIT_TEST_PASS(gs1_lint_packagetype, "WT");
	UNIT_TEST_PASS(gs1_lint_packagetype, "WU");
	UNIT_TEST_PASS(gs1_lint_packagetype, "WV");
	UNIT_TEST_PASS(gs1_lint_packagetype, "WW");
	UNIT_TEST_PASS(gs1_lint_packagetype, "WX");
	UNIT_TEST_PASS(gs1_lint_packagetype, "WY");
	UNIT_TEST_PASS(gs1_lint_packagetype, "WZ");
	UNIT_TEST_PASS(gs1_lint_packagetype, "X11");
	UNIT_TEST_PASS(gs1_lint_packagetype, "X12");
	UNIT_TEST_PASS(gs1_lint_packagetype, "X15");
	UNIT_TEST_PASS(gs1_lint_packagetype, "X16");
	UNIT_TEST_PASS(gs1_lint_packagetype, "X17");
	UNIT_TEST_PASS(gs1_lint_packagetype, "X18");
	UNIT_TEST_PASS(gs1_lint_packagetype, "X19");
	UNIT_TEST_PASS(gs1_lint_packagetype, "X20");
	UNIT_TEST_PASS(gs1_lint_packagetype, "X3");
	UNIT_TEST_PASS(gs1_lint_packagetype, "XA");
	UNIT_TEST_PASS(gs1_lint_packagetype, "XB");
	UNIT_TEST_PASS(gs1_lint_packagetype, "XC");
	UNIT_TEST_PASS(gs1_lint_packagetype, "XD");
	UNIT_TEST_PASS(gs1_lint_packagetype, "XF");
	UNIT_TEST_PASS(gs1_lint_packagetype, "XG");
	UNIT_TEST_PASS(gs1_lint_packagetype, "XH");
	UNIT_TEST_PASS(gs1_lint_packagetype, "XJ");
	UNIT_TEST_PASS(gs1_lint_packagetype, "XK");
	UNIT_TEST_PASS(gs1_lint_packagetype, "YA");
	UNIT_TEST_PASS(gs1_lint_packagetype, "YB");
	UNIT_TEST_PASS(gs1_lint_packagetype, "YC");
	UNIT_TEST_PASS(gs1_lint_packagetype, "YD");
	UNIT_TEST_PASS(gs1_lint_packagetype, "YF");
	UNIT_TEST_PASS(gs1_lint_packagetype, "YG");
	UNIT_TEST_PASS(gs1_lint_packagetype, "YH");
	UNIT_TEST_PASS(gs1_lint_packagetype, "YJ");
	UNIT_TEST_PASS(gs1_lint_packagetype, "YK");
	UNIT_TEST_PASS(gs1_lint_packagetype, "YL");
	UNIT_TEST_PASS(gs1_lint_packagetype, "YM");
	UNIT_TEST_PASS(gs1_lint_packagetype, "YN");
	UNIT_TEST_PASS(gs1_lint_packagetype, "YP");
	UNIT_TEST_PASS(gs1_lint_packagetype, "YQ");
	UNIT_TEST_PASS(gs1_lint_packagetype, "YR");
	UNIT_TEST_PASS(gs1_lint_packagetype, "YS");
	UNIT_TEST_PASS(gs1_lint_packagetype, "YT");
	UNIT_TEST_PASS(gs1_lint_packagetype, "YV");
	UNIT_TEST_PASS(gs1_lint_packagetype, "YW");
	UNIT_TEST_PASS(gs1_lint_packagetype, "YX");
	UNIT_TEST_PASS(gs1_lint_packagetype, "YY");
	UNIT_TEST_PASS(gs1_lint_packagetype, "YZ");
	UNIT_TEST_PASS(gs1_lint_packagetype, "ZA");
	UNIT_TEST_PASS(gs1_lint_packagetype, "ZB");
	UNIT_TEST_PASS(gs1_lint_packagetype, "ZC");
	UNIT_TEST_PASS(gs1_lint_packagetype, "ZD");
	UNIT_TEST_PASS(gs1_lint_packagetype, "ZF");
	UNIT_TEST_PASS(gs1_lint_packagetype, "ZG");
	UNIT_TEST_PASS(gs1_lint_packagetype, "ZH");
	UNIT_TEST_PASS(gs1_lint_packagetype, "ZJ");
	UNIT_TEST_PASS(gs1_lint_packagetype, "ZK");
	UNIT_TEST_PASS(gs1_lint_packagetype, "ZL");
	UNIT_TEST_PASS(gs1_lint_packagetype, "ZM");
	UNIT_TEST_PASS(gs1_lint_packagetype, "ZN");
	UNIT_TEST_PASS(gs1_lint_packagetype, "ZP");
	UNIT_TEST_PASS(gs1_lint_packagetype, "ZQ");
	UNIT_TEST_PASS(gs1_lint_packagetype, "ZR");
	UNIT_TEST_PASS(gs1_lint_packagetype, "ZS");
	UNIT_TEST_PASS(gs1_lint_packagetype, "ZT");
	UNIT_TEST_PASS(gs1_lint_packagetype, "ZU");
	UNIT_TEST_PASS(gs1_lint_packagetype, "ZV");
	UNIT_TEST_PASS(gs1_lint_packagetype, "ZW");
	UNIT_TEST_PASS(gs1_lint_packagetype, "ZX");
	UNIT_TEST_PASS(gs1_lint_packagetype, "ZY");
	UNIT_TEST_PASS(gs1_lint_packagetype, "ZZ");

}

#endif  /* UNIT_TESTS */
