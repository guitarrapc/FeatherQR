/*
 * GS1 Barcode Syntax Dictionary. Copyright (c) 2022-2024 GS1 AISBL.
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
 * @file lint_iso3166alpha2.c
 *
 * @brief The `iso3166alpha2` linter ensures that the data represents an ISO
 * 3166 "alpha-2" country code.
 *
 * @remark The two-character country codes are defined by [ISO 3166-1: Codes for the representation of names of countries and their subdivisions - Part 1: Country code](https://www.iso.org/standard/72482.html) as the "alpha-2" codes.
 *
 */

#include <assert.h>
#include <string.h>
#include <stdint.h>

#include "gs1syntaxdictionary.h"
#include "gs1syntaxdictionary-utils.h"


/*
 * Include a header containing a replacement lookup function, if we are told
 * to.
 *
 */
#ifdef GS1_LINTER_CUSTOM_ISO3166ALPHA2_LOOKUP_H
#define xstr(s) str(s)
#define str(s) #s
#include xstr(GS1_LINTER_CUSTOM_ISO3166ALPHA2_LOOKUP_H)
#endif


/**
 * Used to validate that an AI component is an ISO 3166 "alpha-2" country
 * code.
 *
 * @note To enable this linter to hook into an alternative ISO 3166 "alpha-2"
 *       lookup function (provided by the user) the
 *       GS1_LINTER_CUSTOM_ISO3166ALPHA2_LOOKUP_H macro may be set to the name of a
 *       header file to be included that defines a custom
 *       `GS1_LINTER_CUSTOM_ISO3166ALPHA2_LOOKUP` macro.
 * @note If provided, the GS1_LINTER_CUSTOM_ISO3166ALPHA2_LOOKUP macro shall invoke
 *       whatever functionality is available in the user-provided lookup
 *       function using the first argument (data) and second argument (data length),
 *       then using the result must assign to third (output) argument as follows:
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
 * @return #GS1_LINTER_NOT_ISO3166_ALPHA2 if the data is not a alpha-2 country code.
 *
 */
GS1_SYNTAX_DICTIONARY_API gs1_lint_err_t gs1_lint_iso3166alpha2(const char* const data, size_t data_len, size_t* const err_pos, size_t* const err_len)
{

	/*
	 * Allow for a custom replacement of the lookup code to be provided.
	 *
	 */
#ifdef GS1_LINTER_CUSTOM_ISO3166ALPHA2_LOOKUP
#define GS1_LINTER_ISO3166ALPHA2_LOOKUP(cc, cc_len, valid) GS1_LINTER_CUSTOM_ISO3166ALPHA2_LOOKUP(cc, cc_len, valid)
#else

	/*
	 *  Set of ISO 3166 alpha-2 country codes
	 *
	 *  MAINTENANCE NOTE:
	 *
	 *  Updates to the ISO 3166 alpha-2 country code list are provided here:
	 *
	 *  https://isotc.iso.org/livelink/livelink?func=ll&objId=16944257&objAction=browse&viewType=1
	 *
	 */
	static const uint64_t iso3166alpha2[] = {
#if __STDC_VERSION__ >= 202311L
		0b0001111010011010111110110111011111110111101111011011101101111011,  // AA-CL: AD-AG AI AL-AM AO AQ-AU AW-AX AZ-BB BD-BJ BL-BO BQ-BT BV-BW BY-CA CC-CD CF-CH CI CK-CL
		0b1110010011111100001000011010100000000001001010110000000001110000,  // CM-EX: CM-CO CR CU-CZ DE DJ DK DM DO DZ EC EE EG-EH ER-ET
		0b0000000000111010100100000000110111111001110111111010100000000000,  // EY-HJ: FI-FJ FK FM FO FR GA-GB GD-GI GL-GN GP-GU GW GY
		0b1011000101100000000110000001111011110000000000100000001011000000,  // HK-JV: HK HM-HN HR HT-HU ID-IE IL-IO IQ-IT JE JM JO-JP
		0b0000000010111000110101000010111110000010100000011111001010111111,  // JW-MH: KE KG-KI KM-KN KP KR KW KY-KZ LA-LC LI LK LR-LV LY MA MC-MH
		0b0011111111111111111010111010010011010010000100000000000010000000,  // MI-OT: MK-NA NC NE-NG NI NL NO-NP NR NU NZ OM
		0b0000001000111100111100011100101010000000000000000000000000000010,  // OU-RF: PA PE-PH PK-PN PR-PT PW PY QA RE
		0b0000000010001010100011111011111111100111010111001101110111111001,  // RG-TR: RO RS RU RW SA-SE SG-SO SR-ST SV SX-SZ TC-TD TF-TH TJ-TO TR
		0b0101100110000010000010000010000011101010101000010000001000000000,  // TS-WD: TT TV-TW TZ UA UG UM US UY UZ VA VC VE VG VI VN VU
		0b0100000000000010000000000000000000000000000000000000100000000000,  // WE-YP: WF WS YE
		0b0001000000100000000000100000000010000000000000000000000000000000,  // YQ-ZZ: YT ZA ZM ZW
#else
		/*
		 *  Fallback for compilers lacking binary literal support.
		 *
		 *  Generated from the above data with:
		 *
		 *     for (size_t i = 0; i < sizeof(iso3166alpha2) / sizeof(iso3166alpha2[0]); i++) { printf("0x%016lx, ", iso3166alpha2[i]); };
		 *
		 */
		0x1e9afb77f7bdbb7b, 0xe4fc21a8012b0070, 0x003a900df9dfa800, 0xb160181ef00202c0,
		0x00b8d42f8281f2bf, 0x3fffeba4d2100080, 0x023cf1ca80000002, 0x008a8fbfe75cddf9,
		0x59820820eaa10200, 0x4002000000000800, 0x1020020080000000
#endif
	};

/// \cond
#define GS1_LINTER_ISO3166ALPHA2_LOOKUP(cc, cc_len, valid) do {					\
	valid = 0;										\
	if (cc_len == 2 && cc[0] >= 'A' && cc[0] <= 'Z' && cc[1] >= 'A' && cc[1] <= 'Z') {	\
		int v = (cc[0] - 'A') * 26 + cc[1] - 'A';					\
		GS1_LINTER_BITFIELD_LOOKUP(v, iso3166alpha2, valid);				\
	}											\
} while (0)
/// \endcond

#endif

	int valid;

	assert(data);

	/*
	 * Ensure that the data is in the list.
	 *
	 */
	GS1_LINTER_ISO3166ALPHA2_LOOKUP(data, data_len, valid);
	if (GS1_LINTER_LIKELY(valid))
		GS1_LINTER_RETURN_OK;

	/*
	 * If not valid then indicate an error.
	 *
	 */
	GS1_LINTER_RETURN_ERROR(
		GS1_LINTER_NOT_ISO3166_ALPHA2,
		0,
		data_len
	);

}


#ifdef UNIT_TESTS

#include "unittest.h"

void test_lint_iso3166alpha2(void)
{

	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "", GS1_LINTER_NOT_ISO3166_ALPHA2, "**");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "A", GS1_LINTER_NOT_ISO3166_ALPHA2, "*A*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "AA", GS1_LINTER_NOT_ISO3166_ALPHA2, "*AA*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "AAA", GS1_LINTER_NOT_ISO3166_ALPHA2, "*AAA*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "_AD", GS1_LINTER_NOT_ISO3166_ALPHA2, "*_AD*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "AD_", GS1_LINTER_NOT_ISO3166_ALPHA2, "*AD_*");

	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "AA", GS1_LINTER_NOT_ISO3166_ALPHA2, "*AA*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "AB", GS1_LINTER_NOT_ISO3166_ALPHA2, "*AB*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "AC", GS1_LINTER_NOT_ISO3166_ALPHA2, "*AC*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "AD");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "AE");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "AF");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "AG");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "AH", GS1_LINTER_NOT_ISO3166_ALPHA2, "*AH*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "AI");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "AJ", GS1_LINTER_NOT_ISO3166_ALPHA2, "*AJ*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "AK", GS1_LINTER_NOT_ISO3166_ALPHA2, "*AK*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "AL");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "AM");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "AN", GS1_LINTER_NOT_ISO3166_ALPHA2, "*AN*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "AO");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "AP", GS1_LINTER_NOT_ISO3166_ALPHA2, "*AP*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "AQ");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "AR");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "AS");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "AT");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "AU");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "AV", GS1_LINTER_NOT_ISO3166_ALPHA2, "*AV*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "AW");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "AX");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "AY", GS1_LINTER_NOT_ISO3166_ALPHA2, "*AY*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "AZ");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "BA");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "BB");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "BC", GS1_LINTER_NOT_ISO3166_ALPHA2, "*BC*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "BD");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "BE");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "BF");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "BG");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "BH");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "BI");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "BJ");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "BK", GS1_LINTER_NOT_ISO3166_ALPHA2, "*BK*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "BL");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "BM");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "BN");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "BO");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "BP", GS1_LINTER_NOT_ISO3166_ALPHA2, "*BP*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "BQ");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "BR");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "BS");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "BT");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "BU", GS1_LINTER_NOT_ISO3166_ALPHA2, "*BU*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "BV");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "BW");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "BX", GS1_LINTER_NOT_ISO3166_ALPHA2, "*BX*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "BY");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "BZ");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "CA");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "CB", GS1_LINTER_NOT_ISO3166_ALPHA2, "*CB*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "CC");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "CD");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "CE", GS1_LINTER_NOT_ISO3166_ALPHA2, "*CE*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "CF");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "CG");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "CH");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "CI");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "CJ", GS1_LINTER_NOT_ISO3166_ALPHA2, "*CJ*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "CK");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "CL");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "CM");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "CN");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "CO");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "CP", GS1_LINTER_NOT_ISO3166_ALPHA2, "*CP*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "CQ", GS1_LINTER_NOT_ISO3166_ALPHA2, "*CQ*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "CR");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "CS", GS1_LINTER_NOT_ISO3166_ALPHA2, "*CS*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "CT", GS1_LINTER_NOT_ISO3166_ALPHA2, "*CT*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "CU");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "CV");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "CW");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "CX");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "CY");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "CZ");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "DA", GS1_LINTER_NOT_ISO3166_ALPHA2, "*DA*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "DB", GS1_LINTER_NOT_ISO3166_ALPHA2, "*DB*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "DC", GS1_LINTER_NOT_ISO3166_ALPHA2, "*DC*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "DD", GS1_LINTER_NOT_ISO3166_ALPHA2, "*DD*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "DE");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "DF", GS1_LINTER_NOT_ISO3166_ALPHA2, "*DF*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "DG", GS1_LINTER_NOT_ISO3166_ALPHA2, "*DG*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "DH", GS1_LINTER_NOT_ISO3166_ALPHA2, "*DH*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "DI", GS1_LINTER_NOT_ISO3166_ALPHA2, "*DI*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "DJ");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "DK");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "DL", GS1_LINTER_NOT_ISO3166_ALPHA2, "*DL*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "DM");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "DN", GS1_LINTER_NOT_ISO3166_ALPHA2, "*DN*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "DO");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "DP", GS1_LINTER_NOT_ISO3166_ALPHA2, "*DP*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "DQ", GS1_LINTER_NOT_ISO3166_ALPHA2, "*DQ*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "DR", GS1_LINTER_NOT_ISO3166_ALPHA2, "*DR*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "DS", GS1_LINTER_NOT_ISO3166_ALPHA2, "*DS*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "DT", GS1_LINTER_NOT_ISO3166_ALPHA2, "*DT*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "DU", GS1_LINTER_NOT_ISO3166_ALPHA2, "*DU*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "DV", GS1_LINTER_NOT_ISO3166_ALPHA2, "*DV*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "DW", GS1_LINTER_NOT_ISO3166_ALPHA2, "*DW*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "DX", GS1_LINTER_NOT_ISO3166_ALPHA2, "*DX*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "DY", GS1_LINTER_NOT_ISO3166_ALPHA2, "*DY*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "DZ");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "EA", GS1_LINTER_NOT_ISO3166_ALPHA2, "*EA*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "EB", GS1_LINTER_NOT_ISO3166_ALPHA2, "*EB*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "EC");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "ED", GS1_LINTER_NOT_ISO3166_ALPHA2, "*ED*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "EE");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "EF", GS1_LINTER_NOT_ISO3166_ALPHA2, "*EF*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "EG");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "EH");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "EI", GS1_LINTER_NOT_ISO3166_ALPHA2, "*EI*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "EJ", GS1_LINTER_NOT_ISO3166_ALPHA2, "*EJ*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "EK", GS1_LINTER_NOT_ISO3166_ALPHA2, "*EK*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "EL", GS1_LINTER_NOT_ISO3166_ALPHA2, "*EL*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "EM", GS1_LINTER_NOT_ISO3166_ALPHA2, "*EM*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "EN", GS1_LINTER_NOT_ISO3166_ALPHA2, "*EN*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "EO", GS1_LINTER_NOT_ISO3166_ALPHA2, "*EO*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "EP", GS1_LINTER_NOT_ISO3166_ALPHA2, "*EP*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "EQ", GS1_LINTER_NOT_ISO3166_ALPHA2, "*EQ*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "ER");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "ES");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "ET");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "EU", GS1_LINTER_NOT_ISO3166_ALPHA2, "*EU*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "EV", GS1_LINTER_NOT_ISO3166_ALPHA2, "*EV*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "EW", GS1_LINTER_NOT_ISO3166_ALPHA2, "*EW*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "EX", GS1_LINTER_NOT_ISO3166_ALPHA2, "*EX*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "EY", GS1_LINTER_NOT_ISO3166_ALPHA2, "*EY*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "EZ", GS1_LINTER_NOT_ISO3166_ALPHA2, "*EZ*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "FA", GS1_LINTER_NOT_ISO3166_ALPHA2, "*FA*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "FB", GS1_LINTER_NOT_ISO3166_ALPHA2, "*FB*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "FC", GS1_LINTER_NOT_ISO3166_ALPHA2, "*FC*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "FD", GS1_LINTER_NOT_ISO3166_ALPHA2, "*FD*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "FE", GS1_LINTER_NOT_ISO3166_ALPHA2, "*FE*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "FF", GS1_LINTER_NOT_ISO3166_ALPHA2, "*FF*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "FG", GS1_LINTER_NOT_ISO3166_ALPHA2, "*FG*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "FH", GS1_LINTER_NOT_ISO3166_ALPHA2, "*FH*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "FI");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "FJ");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "FK");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "FL", GS1_LINTER_NOT_ISO3166_ALPHA2, "*FL*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "FM");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "FN", GS1_LINTER_NOT_ISO3166_ALPHA2, "*FN*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "FO");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "FP", GS1_LINTER_NOT_ISO3166_ALPHA2, "*FP*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "FQ", GS1_LINTER_NOT_ISO3166_ALPHA2, "*FQ*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "FR");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "FS", GS1_LINTER_NOT_ISO3166_ALPHA2, "*FS*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "FT", GS1_LINTER_NOT_ISO3166_ALPHA2, "*FT*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "FU", GS1_LINTER_NOT_ISO3166_ALPHA2, "*FU*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "FV", GS1_LINTER_NOT_ISO3166_ALPHA2, "*FV*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "FW", GS1_LINTER_NOT_ISO3166_ALPHA2, "*FW*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "FX", GS1_LINTER_NOT_ISO3166_ALPHA2, "*FX*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "FY", GS1_LINTER_NOT_ISO3166_ALPHA2, "*FY*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "FZ", GS1_LINTER_NOT_ISO3166_ALPHA2, "*FZ*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "GA");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "GB");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "GC", GS1_LINTER_NOT_ISO3166_ALPHA2, "*GC*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "GD");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "GE");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "GF");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "GG");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "GH");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "GI");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "GJ", GS1_LINTER_NOT_ISO3166_ALPHA2, "*GJ*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "GK", GS1_LINTER_NOT_ISO3166_ALPHA2, "*GK*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "GL");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "GM");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "GN");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "GO", GS1_LINTER_NOT_ISO3166_ALPHA2, "*GO*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "GP");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "GQ");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "GR");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "GS");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "GT");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "GU");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "GV", GS1_LINTER_NOT_ISO3166_ALPHA2, "*GV*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "GW");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "GX", GS1_LINTER_NOT_ISO3166_ALPHA2, "*GX*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "GY");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "GZ", GS1_LINTER_NOT_ISO3166_ALPHA2, "*GZ*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "HA", GS1_LINTER_NOT_ISO3166_ALPHA2, "*HA*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "HB", GS1_LINTER_NOT_ISO3166_ALPHA2, "*HB*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "HC", GS1_LINTER_NOT_ISO3166_ALPHA2, "*HC*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "HD", GS1_LINTER_NOT_ISO3166_ALPHA2, "*HD*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "HE", GS1_LINTER_NOT_ISO3166_ALPHA2, "*HE*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "HF", GS1_LINTER_NOT_ISO3166_ALPHA2, "*HF*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "HG", GS1_LINTER_NOT_ISO3166_ALPHA2, "*HG*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "HH", GS1_LINTER_NOT_ISO3166_ALPHA2, "*HH*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "HI", GS1_LINTER_NOT_ISO3166_ALPHA2, "*HI*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "HJ", GS1_LINTER_NOT_ISO3166_ALPHA2, "*HJ*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "HK");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "HL", GS1_LINTER_NOT_ISO3166_ALPHA2, "*HL*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "HM");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "HN");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "HO", GS1_LINTER_NOT_ISO3166_ALPHA2, "*HO*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "HP", GS1_LINTER_NOT_ISO3166_ALPHA2, "*HP*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "HQ", GS1_LINTER_NOT_ISO3166_ALPHA2, "*HQ*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "HR");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "HS", GS1_LINTER_NOT_ISO3166_ALPHA2, "*HS*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "HT");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "HU");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "HV", GS1_LINTER_NOT_ISO3166_ALPHA2, "*HV*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "HW", GS1_LINTER_NOT_ISO3166_ALPHA2, "*HW*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "HX", GS1_LINTER_NOT_ISO3166_ALPHA2, "*HX*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "HY", GS1_LINTER_NOT_ISO3166_ALPHA2, "*HY*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "HZ", GS1_LINTER_NOT_ISO3166_ALPHA2, "*HZ*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "IA", GS1_LINTER_NOT_ISO3166_ALPHA2, "*IA*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "IB", GS1_LINTER_NOT_ISO3166_ALPHA2, "*IB*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "IC", GS1_LINTER_NOT_ISO3166_ALPHA2, "*IC*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "ID");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "IE");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "IF", GS1_LINTER_NOT_ISO3166_ALPHA2, "*IF*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "IG", GS1_LINTER_NOT_ISO3166_ALPHA2, "*IG*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "IH", GS1_LINTER_NOT_ISO3166_ALPHA2, "*IH*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "II", GS1_LINTER_NOT_ISO3166_ALPHA2, "*II*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "IJ", GS1_LINTER_NOT_ISO3166_ALPHA2, "*IJ*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "IK", GS1_LINTER_NOT_ISO3166_ALPHA2, "*IK*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "IL");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "IM");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "IN");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "IO");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "IP", GS1_LINTER_NOT_ISO3166_ALPHA2, "*IP*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "IQ");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "IR");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "IS");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "IT");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "IU", GS1_LINTER_NOT_ISO3166_ALPHA2, "*IU*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "IV", GS1_LINTER_NOT_ISO3166_ALPHA2, "*IV*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "IW", GS1_LINTER_NOT_ISO3166_ALPHA2, "*IW*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "IX", GS1_LINTER_NOT_ISO3166_ALPHA2, "*IX*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "IY", GS1_LINTER_NOT_ISO3166_ALPHA2, "*IY*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "IZ", GS1_LINTER_NOT_ISO3166_ALPHA2, "*IZ*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "JA", GS1_LINTER_NOT_ISO3166_ALPHA2, "*JA*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "JB", GS1_LINTER_NOT_ISO3166_ALPHA2, "*JB*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "JC", GS1_LINTER_NOT_ISO3166_ALPHA2, "*JC*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "JD", GS1_LINTER_NOT_ISO3166_ALPHA2, "*JD*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "JE");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "JF", GS1_LINTER_NOT_ISO3166_ALPHA2, "*JF*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "JG", GS1_LINTER_NOT_ISO3166_ALPHA2, "*JG*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "JH", GS1_LINTER_NOT_ISO3166_ALPHA2, "*JH*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "JI", GS1_LINTER_NOT_ISO3166_ALPHA2, "*JI*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "JJ", GS1_LINTER_NOT_ISO3166_ALPHA2, "*JJ*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "JK", GS1_LINTER_NOT_ISO3166_ALPHA2, "*JK*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "JL", GS1_LINTER_NOT_ISO3166_ALPHA2, "*JL*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "JM");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "JN", GS1_LINTER_NOT_ISO3166_ALPHA2, "*JN*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "JO");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "JP");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "JQ", GS1_LINTER_NOT_ISO3166_ALPHA2, "*JQ*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "JR", GS1_LINTER_NOT_ISO3166_ALPHA2, "*JR*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "JS", GS1_LINTER_NOT_ISO3166_ALPHA2, "*JS*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "JT", GS1_LINTER_NOT_ISO3166_ALPHA2, "*JT*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "JU", GS1_LINTER_NOT_ISO3166_ALPHA2, "*JU*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "JV", GS1_LINTER_NOT_ISO3166_ALPHA2, "*JV*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "JW", GS1_LINTER_NOT_ISO3166_ALPHA2, "*JW*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "JX", GS1_LINTER_NOT_ISO3166_ALPHA2, "*JX*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "JY", GS1_LINTER_NOT_ISO3166_ALPHA2, "*JY*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "JZ", GS1_LINTER_NOT_ISO3166_ALPHA2, "*JZ*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "KA", GS1_LINTER_NOT_ISO3166_ALPHA2, "*KA*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "KB", GS1_LINTER_NOT_ISO3166_ALPHA2, "*KB*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "KC", GS1_LINTER_NOT_ISO3166_ALPHA2, "*KC*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "KD", GS1_LINTER_NOT_ISO3166_ALPHA2, "*KD*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "KE");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "KF", GS1_LINTER_NOT_ISO3166_ALPHA2, "*KF*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "KG");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "KH");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "KI");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "KJ", GS1_LINTER_NOT_ISO3166_ALPHA2, "*KJ*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "KK", GS1_LINTER_NOT_ISO3166_ALPHA2, "*KK*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "KL", GS1_LINTER_NOT_ISO3166_ALPHA2, "*KL*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "KM");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "KN");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "KO", GS1_LINTER_NOT_ISO3166_ALPHA2, "*KO*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "KP");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "KQ", GS1_LINTER_NOT_ISO3166_ALPHA2, "*KQ*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "KR");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "KS", GS1_LINTER_NOT_ISO3166_ALPHA2, "*KS*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "KT", GS1_LINTER_NOT_ISO3166_ALPHA2, "*KT*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "KU", GS1_LINTER_NOT_ISO3166_ALPHA2, "*KU*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "KV", GS1_LINTER_NOT_ISO3166_ALPHA2, "*KV*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "KW");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "KX", GS1_LINTER_NOT_ISO3166_ALPHA2, "*KX*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "KY");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "KZ");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "LA");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "LB");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "LC");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "LD", GS1_LINTER_NOT_ISO3166_ALPHA2, "*LD*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "LE", GS1_LINTER_NOT_ISO3166_ALPHA2, "*LE*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "LF", GS1_LINTER_NOT_ISO3166_ALPHA2, "*LF*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "LG", GS1_LINTER_NOT_ISO3166_ALPHA2, "*LG*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "LH", GS1_LINTER_NOT_ISO3166_ALPHA2, "*LH*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "LI");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "LJ", GS1_LINTER_NOT_ISO3166_ALPHA2, "*LJ*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "LK");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "LL", GS1_LINTER_NOT_ISO3166_ALPHA2, "*LL*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "LM", GS1_LINTER_NOT_ISO3166_ALPHA2, "*LM*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "LN", GS1_LINTER_NOT_ISO3166_ALPHA2, "*LN*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "LO", GS1_LINTER_NOT_ISO3166_ALPHA2, "*LO*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "LP", GS1_LINTER_NOT_ISO3166_ALPHA2, "*LP*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "LQ", GS1_LINTER_NOT_ISO3166_ALPHA2, "*LQ*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "LR");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "LS");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "LT");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "LU");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "LV");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "LW", GS1_LINTER_NOT_ISO3166_ALPHA2, "*LW*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "LX", GS1_LINTER_NOT_ISO3166_ALPHA2, "*LX*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "LY");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "LZ", GS1_LINTER_NOT_ISO3166_ALPHA2, "*LZ*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "MA");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "MB", GS1_LINTER_NOT_ISO3166_ALPHA2, "*MB*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "MC");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "MD");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "ME");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "MF");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "MG");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "MH");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "MI", GS1_LINTER_NOT_ISO3166_ALPHA2, "*MI*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "MJ", GS1_LINTER_NOT_ISO3166_ALPHA2, "*MJ*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "MK");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "ML");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "MM");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "MN");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "MO");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "MP");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "MQ");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "MR");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "MS");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "MT");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "MU");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "MV");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "MW");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "MX");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "MY");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "MZ");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "NA");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "NB", GS1_LINTER_NOT_ISO3166_ALPHA2, "*NB*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "NC");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "ND", GS1_LINTER_NOT_ISO3166_ALPHA2, "*ND*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "NE");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "NF");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "NG");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "NH", GS1_LINTER_NOT_ISO3166_ALPHA2, "*NH*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "NI");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "NJ", GS1_LINTER_NOT_ISO3166_ALPHA2, "*NJ*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "NK", GS1_LINTER_NOT_ISO3166_ALPHA2, "*NK*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "NL");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "NM", GS1_LINTER_NOT_ISO3166_ALPHA2, "*NM*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "NN", GS1_LINTER_NOT_ISO3166_ALPHA2, "*NN*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "NO");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "NP");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "NQ", GS1_LINTER_NOT_ISO3166_ALPHA2, "*NQ*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "NR");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "NS", GS1_LINTER_NOT_ISO3166_ALPHA2, "*NS*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "NT", GS1_LINTER_NOT_ISO3166_ALPHA2, "*NT*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "NU");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "NV", GS1_LINTER_NOT_ISO3166_ALPHA2, "*NV*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "NW", GS1_LINTER_NOT_ISO3166_ALPHA2, "*NW*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "NX", GS1_LINTER_NOT_ISO3166_ALPHA2, "*NX*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "NY", GS1_LINTER_NOT_ISO3166_ALPHA2, "*NY*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "NZ");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "OA", GS1_LINTER_NOT_ISO3166_ALPHA2, "*OA*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "OB", GS1_LINTER_NOT_ISO3166_ALPHA2, "*OB*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "OC", GS1_LINTER_NOT_ISO3166_ALPHA2, "*OC*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "OD", GS1_LINTER_NOT_ISO3166_ALPHA2, "*OD*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "OE", GS1_LINTER_NOT_ISO3166_ALPHA2, "*OE*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "OF", GS1_LINTER_NOT_ISO3166_ALPHA2, "*OF*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "OG", GS1_LINTER_NOT_ISO3166_ALPHA2, "*OG*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "OH", GS1_LINTER_NOT_ISO3166_ALPHA2, "*OH*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "OI", GS1_LINTER_NOT_ISO3166_ALPHA2, "*OI*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "OJ", GS1_LINTER_NOT_ISO3166_ALPHA2, "*OJ*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "OK", GS1_LINTER_NOT_ISO3166_ALPHA2, "*OK*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "OL", GS1_LINTER_NOT_ISO3166_ALPHA2, "*OL*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "OM");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "ON", GS1_LINTER_NOT_ISO3166_ALPHA2, "*ON*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "OO", GS1_LINTER_NOT_ISO3166_ALPHA2, "*OO*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "OP", GS1_LINTER_NOT_ISO3166_ALPHA2, "*OP*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "OQ", GS1_LINTER_NOT_ISO3166_ALPHA2, "*OQ*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "OR", GS1_LINTER_NOT_ISO3166_ALPHA2, "*OR*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "OS", GS1_LINTER_NOT_ISO3166_ALPHA2, "*OS*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "OT", GS1_LINTER_NOT_ISO3166_ALPHA2, "*OT*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "OU", GS1_LINTER_NOT_ISO3166_ALPHA2, "*OU*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "OV", GS1_LINTER_NOT_ISO3166_ALPHA2, "*OV*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "OW", GS1_LINTER_NOT_ISO3166_ALPHA2, "*OW*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "OX", GS1_LINTER_NOT_ISO3166_ALPHA2, "*OX*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "OY", GS1_LINTER_NOT_ISO3166_ALPHA2, "*OY*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "OZ", GS1_LINTER_NOT_ISO3166_ALPHA2, "*OZ*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "PA");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "PB", GS1_LINTER_NOT_ISO3166_ALPHA2, "*PB*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "PC", GS1_LINTER_NOT_ISO3166_ALPHA2, "*PC*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "PD", GS1_LINTER_NOT_ISO3166_ALPHA2, "*PD*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "PE");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "PF");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "PG");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "PH");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "PI", GS1_LINTER_NOT_ISO3166_ALPHA2, "*PI*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "PJ", GS1_LINTER_NOT_ISO3166_ALPHA2, "*PJ*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "PK");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "PL");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "PM");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "PN");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "PO", GS1_LINTER_NOT_ISO3166_ALPHA2, "*PO*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "PP", GS1_LINTER_NOT_ISO3166_ALPHA2, "*PP*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "PQ", GS1_LINTER_NOT_ISO3166_ALPHA2, "*PQ*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "PR");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "PS");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "PT");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "PU", GS1_LINTER_NOT_ISO3166_ALPHA2, "*PU*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "PV", GS1_LINTER_NOT_ISO3166_ALPHA2, "*PV*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "PW");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "PX", GS1_LINTER_NOT_ISO3166_ALPHA2, "*PX*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "PY");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "PZ", GS1_LINTER_NOT_ISO3166_ALPHA2, "*PZ*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "QA");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "QB", GS1_LINTER_NOT_ISO3166_ALPHA2, "*QB*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "QC", GS1_LINTER_NOT_ISO3166_ALPHA2, "*QC*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "QD", GS1_LINTER_NOT_ISO3166_ALPHA2, "*QD*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "QE", GS1_LINTER_NOT_ISO3166_ALPHA2, "*QE*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "QF", GS1_LINTER_NOT_ISO3166_ALPHA2, "*QF*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "QG", GS1_LINTER_NOT_ISO3166_ALPHA2, "*QG*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "QH", GS1_LINTER_NOT_ISO3166_ALPHA2, "*QH*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "QI", GS1_LINTER_NOT_ISO3166_ALPHA2, "*QI*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "QJ", GS1_LINTER_NOT_ISO3166_ALPHA2, "*QJ*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "QK", GS1_LINTER_NOT_ISO3166_ALPHA2, "*QK*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "QL", GS1_LINTER_NOT_ISO3166_ALPHA2, "*QL*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "QM", GS1_LINTER_NOT_ISO3166_ALPHA2, "*QM*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "QN", GS1_LINTER_NOT_ISO3166_ALPHA2, "*QN*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "QO", GS1_LINTER_NOT_ISO3166_ALPHA2, "*QO*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "QP", GS1_LINTER_NOT_ISO3166_ALPHA2, "*QP*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "QQ", GS1_LINTER_NOT_ISO3166_ALPHA2, "*QQ*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "QR", GS1_LINTER_NOT_ISO3166_ALPHA2, "*QR*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "QS", GS1_LINTER_NOT_ISO3166_ALPHA2, "*QS*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "QT", GS1_LINTER_NOT_ISO3166_ALPHA2, "*QT*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "QU", GS1_LINTER_NOT_ISO3166_ALPHA2, "*QU*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "QV", GS1_LINTER_NOT_ISO3166_ALPHA2, "*QV*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "QW", GS1_LINTER_NOT_ISO3166_ALPHA2, "*QW*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "QX", GS1_LINTER_NOT_ISO3166_ALPHA2, "*QX*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "QY", GS1_LINTER_NOT_ISO3166_ALPHA2, "*QY*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "QZ", GS1_LINTER_NOT_ISO3166_ALPHA2, "*QZ*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "RA", GS1_LINTER_NOT_ISO3166_ALPHA2, "*RA*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "RB", GS1_LINTER_NOT_ISO3166_ALPHA2, "*RB*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "RC", GS1_LINTER_NOT_ISO3166_ALPHA2, "*RC*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "RD", GS1_LINTER_NOT_ISO3166_ALPHA2, "*RD*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "RE");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "RF", GS1_LINTER_NOT_ISO3166_ALPHA2, "*RF*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "RG", GS1_LINTER_NOT_ISO3166_ALPHA2, "*RG*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "RH", GS1_LINTER_NOT_ISO3166_ALPHA2, "*RH*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "RI", GS1_LINTER_NOT_ISO3166_ALPHA2, "*RI*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "RJ", GS1_LINTER_NOT_ISO3166_ALPHA2, "*RJ*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "RK", GS1_LINTER_NOT_ISO3166_ALPHA2, "*RK*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "RL", GS1_LINTER_NOT_ISO3166_ALPHA2, "*RL*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "RM", GS1_LINTER_NOT_ISO3166_ALPHA2, "*RM*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "RN", GS1_LINTER_NOT_ISO3166_ALPHA2, "*RN*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "RO");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "RP", GS1_LINTER_NOT_ISO3166_ALPHA2, "*RP*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "RQ", GS1_LINTER_NOT_ISO3166_ALPHA2, "*RQ*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "RR", GS1_LINTER_NOT_ISO3166_ALPHA2, "*RR*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "RS");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "RT", GS1_LINTER_NOT_ISO3166_ALPHA2, "*RT*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "RU");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "RV", GS1_LINTER_NOT_ISO3166_ALPHA2, "*RV*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "RW");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "RX", GS1_LINTER_NOT_ISO3166_ALPHA2, "*RX*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "RY", GS1_LINTER_NOT_ISO3166_ALPHA2, "*RY*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "RZ", GS1_LINTER_NOT_ISO3166_ALPHA2, "*RZ*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "SA");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "SB");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "SC");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "SD");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "SE");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "SF", GS1_LINTER_NOT_ISO3166_ALPHA2, "*SF*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "SG");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "SH");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "SI");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "SJ");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "SK");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "SL");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "SM");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "SN");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "SO");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "SP", GS1_LINTER_NOT_ISO3166_ALPHA2, "*SP*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "SQ", GS1_LINTER_NOT_ISO3166_ALPHA2, "*SQ*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "SR");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "SS");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "ST");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "SU", GS1_LINTER_NOT_ISO3166_ALPHA2, "*SU*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "SV");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "SW", GS1_LINTER_NOT_ISO3166_ALPHA2, "*SW*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "SX");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "SY");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "SZ");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "TA", GS1_LINTER_NOT_ISO3166_ALPHA2, "*TA*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "TB", GS1_LINTER_NOT_ISO3166_ALPHA2, "*TB*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "TC");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "TD");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "TE", GS1_LINTER_NOT_ISO3166_ALPHA2, "*TE*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "TF");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "TG");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "TH");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "TI", GS1_LINTER_NOT_ISO3166_ALPHA2, "*TI*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "TJ");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "TK");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "TL");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "TM");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "TN");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "TO");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "TP", GS1_LINTER_NOT_ISO3166_ALPHA2, "*TP*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "TQ", GS1_LINTER_NOT_ISO3166_ALPHA2, "*TQ*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "TR");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "TS", GS1_LINTER_NOT_ISO3166_ALPHA2, "*TS*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "TT");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "TU", GS1_LINTER_NOT_ISO3166_ALPHA2, "*TU*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "TV");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "TW");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "TX", GS1_LINTER_NOT_ISO3166_ALPHA2, "*TX*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "TY", GS1_LINTER_NOT_ISO3166_ALPHA2, "*TY*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "TZ");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "UA");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "UB", GS1_LINTER_NOT_ISO3166_ALPHA2, "*UB*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "UC", GS1_LINTER_NOT_ISO3166_ALPHA2, "*UC*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "UD", GS1_LINTER_NOT_ISO3166_ALPHA2, "*UD*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "UE", GS1_LINTER_NOT_ISO3166_ALPHA2, "*UE*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "UF", GS1_LINTER_NOT_ISO3166_ALPHA2, "*UF*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "UG");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "UH", GS1_LINTER_NOT_ISO3166_ALPHA2, "*UH*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "UI", GS1_LINTER_NOT_ISO3166_ALPHA2, "*UI*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "UJ", GS1_LINTER_NOT_ISO3166_ALPHA2, "*UJ*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "UK", GS1_LINTER_NOT_ISO3166_ALPHA2, "*UK*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "UL", GS1_LINTER_NOT_ISO3166_ALPHA2, "*UL*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "UM");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "UN", GS1_LINTER_NOT_ISO3166_ALPHA2, "*UN*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "UO", GS1_LINTER_NOT_ISO3166_ALPHA2, "*UO*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "UP", GS1_LINTER_NOT_ISO3166_ALPHA2, "*UP*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "UQ", GS1_LINTER_NOT_ISO3166_ALPHA2, "*UQ*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "UR", GS1_LINTER_NOT_ISO3166_ALPHA2, "*UR*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "US");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "UT", GS1_LINTER_NOT_ISO3166_ALPHA2, "*UT*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "UU", GS1_LINTER_NOT_ISO3166_ALPHA2, "*UU*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "UV", GS1_LINTER_NOT_ISO3166_ALPHA2, "*UV*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "UW", GS1_LINTER_NOT_ISO3166_ALPHA2, "*UW*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "UX", GS1_LINTER_NOT_ISO3166_ALPHA2, "*UX*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "UY");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "UZ");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "VA");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "VB", GS1_LINTER_NOT_ISO3166_ALPHA2, "*VB*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "VC");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "VD", GS1_LINTER_NOT_ISO3166_ALPHA2, "*VD*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "VE");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "VF", GS1_LINTER_NOT_ISO3166_ALPHA2, "*VF*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "VG");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "VH", GS1_LINTER_NOT_ISO3166_ALPHA2, "*VH*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "VI");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "VJ", GS1_LINTER_NOT_ISO3166_ALPHA2, "*VJ*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "VK", GS1_LINTER_NOT_ISO3166_ALPHA2, "*VK*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "VL", GS1_LINTER_NOT_ISO3166_ALPHA2, "*VL*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "VM", GS1_LINTER_NOT_ISO3166_ALPHA2, "*VM*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "VN");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "VO", GS1_LINTER_NOT_ISO3166_ALPHA2, "*VO*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "VP", GS1_LINTER_NOT_ISO3166_ALPHA2, "*VP*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "VQ", GS1_LINTER_NOT_ISO3166_ALPHA2, "*VQ*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "VR", GS1_LINTER_NOT_ISO3166_ALPHA2, "*VR*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "VS", GS1_LINTER_NOT_ISO3166_ALPHA2, "*VS*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "VT", GS1_LINTER_NOT_ISO3166_ALPHA2, "*VT*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "VU");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "VV", GS1_LINTER_NOT_ISO3166_ALPHA2, "*VV*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "VW", GS1_LINTER_NOT_ISO3166_ALPHA2, "*VW*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "VX", GS1_LINTER_NOT_ISO3166_ALPHA2, "*VX*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "VY", GS1_LINTER_NOT_ISO3166_ALPHA2, "*VY*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "VZ", GS1_LINTER_NOT_ISO3166_ALPHA2, "*VZ*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "WA", GS1_LINTER_NOT_ISO3166_ALPHA2, "*WA*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "WB", GS1_LINTER_NOT_ISO3166_ALPHA2, "*WB*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "WC", GS1_LINTER_NOT_ISO3166_ALPHA2, "*WC*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "WD", GS1_LINTER_NOT_ISO3166_ALPHA2, "*WD*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "WE", GS1_LINTER_NOT_ISO3166_ALPHA2, "*WE*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "WF");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "WG", GS1_LINTER_NOT_ISO3166_ALPHA2, "*WG*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "WH", GS1_LINTER_NOT_ISO3166_ALPHA2, "*WH*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "WI", GS1_LINTER_NOT_ISO3166_ALPHA2, "*WI*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "WJ", GS1_LINTER_NOT_ISO3166_ALPHA2, "*WJ*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "WK", GS1_LINTER_NOT_ISO3166_ALPHA2, "*WK*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "WL", GS1_LINTER_NOT_ISO3166_ALPHA2, "*WL*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "WM", GS1_LINTER_NOT_ISO3166_ALPHA2, "*WM*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "WN", GS1_LINTER_NOT_ISO3166_ALPHA2, "*WN*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "WO", GS1_LINTER_NOT_ISO3166_ALPHA2, "*WO*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "WP", GS1_LINTER_NOT_ISO3166_ALPHA2, "*WP*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "WQ", GS1_LINTER_NOT_ISO3166_ALPHA2, "*WQ*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "WR", GS1_LINTER_NOT_ISO3166_ALPHA2, "*WR*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "WS");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "WT", GS1_LINTER_NOT_ISO3166_ALPHA2, "*WT*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "WU", GS1_LINTER_NOT_ISO3166_ALPHA2, "*WU*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "WV", GS1_LINTER_NOT_ISO3166_ALPHA2, "*WV*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "WW", GS1_LINTER_NOT_ISO3166_ALPHA2, "*WW*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "WX", GS1_LINTER_NOT_ISO3166_ALPHA2, "*WX*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "WY", GS1_LINTER_NOT_ISO3166_ALPHA2, "*WY*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "WZ", GS1_LINTER_NOT_ISO3166_ALPHA2, "*WZ*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "XA", GS1_LINTER_NOT_ISO3166_ALPHA2, "*XA*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "XB", GS1_LINTER_NOT_ISO3166_ALPHA2, "*XB*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "XC", GS1_LINTER_NOT_ISO3166_ALPHA2, "*XC*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "XD", GS1_LINTER_NOT_ISO3166_ALPHA2, "*XD*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "XE", GS1_LINTER_NOT_ISO3166_ALPHA2, "*XE*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "XF", GS1_LINTER_NOT_ISO3166_ALPHA2, "*XF*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "XG", GS1_LINTER_NOT_ISO3166_ALPHA2, "*XG*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "XH", GS1_LINTER_NOT_ISO3166_ALPHA2, "*XH*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "XI", GS1_LINTER_NOT_ISO3166_ALPHA2, "*XI*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "XJ", GS1_LINTER_NOT_ISO3166_ALPHA2, "*XJ*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "XK", GS1_LINTER_NOT_ISO3166_ALPHA2, "*XK*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "XL", GS1_LINTER_NOT_ISO3166_ALPHA2, "*XL*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "XM", GS1_LINTER_NOT_ISO3166_ALPHA2, "*XM*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "XN", GS1_LINTER_NOT_ISO3166_ALPHA2, "*XN*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "XO", GS1_LINTER_NOT_ISO3166_ALPHA2, "*XO*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "XP", GS1_LINTER_NOT_ISO3166_ALPHA2, "*XP*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "XQ", GS1_LINTER_NOT_ISO3166_ALPHA2, "*XQ*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "XR", GS1_LINTER_NOT_ISO3166_ALPHA2, "*XR*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "XS", GS1_LINTER_NOT_ISO3166_ALPHA2, "*XS*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "XT", GS1_LINTER_NOT_ISO3166_ALPHA2, "*XT*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "XU", GS1_LINTER_NOT_ISO3166_ALPHA2, "*XU*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "XV", GS1_LINTER_NOT_ISO3166_ALPHA2, "*XV*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "XW", GS1_LINTER_NOT_ISO3166_ALPHA2, "*XW*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "XX", GS1_LINTER_NOT_ISO3166_ALPHA2, "*XX*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "XY", GS1_LINTER_NOT_ISO3166_ALPHA2, "*XY*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "XZ", GS1_LINTER_NOT_ISO3166_ALPHA2, "*XZ*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "YA", GS1_LINTER_NOT_ISO3166_ALPHA2, "*YA*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "YB", GS1_LINTER_NOT_ISO3166_ALPHA2, "*YB*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "YC", GS1_LINTER_NOT_ISO3166_ALPHA2, "*YC*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "YD", GS1_LINTER_NOT_ISO3166_ALPHA2, "*YD*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "YE");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "YF", GS1_LINTER_NOT_ISO3166_ALPHA2, "*YF*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "YG", GS1_LINTER_NOT_ISO3166_ALPHA2, "*YG*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "YH", GS1_LINTER_NOT_ISO3166_ALPHA2, "*YH*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "YI", GS1_LINTER_NOT_ISO3166_ALPHA2, "*YI*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "YJ", GS1_LINTER_NOT_ISO3166_ALPHA2, "*YJ*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "YK", GS1_LINTER_NOT_ISO3166_ALPHA2, "*YK*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "YL", GS1_LINTER_NOT_ISO3166_ALPHA2, "*YL*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "YM", GS1_LINTER_NOT_ISO3166_ALPHA2, "*YM*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "YN", GS1_LINTER_NOT_ISO3166_ALPHA2, "*YN*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "YO", GS1_LINTER_NOT_ISO3166_ALPHA2, "*YO*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "YP", GS1_LINTER_NOT_ISO3166_ALPHA2, "*YP*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "YQ", GS1_LINTER_NOT_ISO3166_ALPHA2, "*YQ*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "YR", GS1_LINTER_NOT_ISO3166_ALPHA2, "*YR*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "YS", GS1_LINTER_NOT_ISO3166_ALPHA2, "*YS*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "YT");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "YU", GS1_LINTER_NOT_ISO3166_ALPHA2, "*YU*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "YV", GS1_LINTER_NOT_ISO3166_ALPHA2, "*YV*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "YW", GS1_LINTER_NOT_ISO3166_ALPHA2, "*YW*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "YX", GS1_LINTER_NOT_ISO3166_ALPHA2, "*YX*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "YY", GS1_LINTER_NOT_ISO3166_ALPHA2, "*YY*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "YZ", GS1_LINTER_NOT_ISO3166_ALPHA2, "*YZ*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "ZA");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "ZB", GS1_LINTER_NOT_ISO3166_ALPHA2, "*ZB*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "ZC", GS1_LINTER_NOT_ISO3166_ALPHA2, "*ZC*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "ZD", GS1_LINTER_NOT_ISO3166_ALPHA2, "*ZD*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "ZE", GS1_LINTER_NOT_ISO3166_ALPHA2, "*ZE*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "ZF", GS1_LINTER_NOT_ISO3166_ALPHA2, "*ZF*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "ZG", GS1_LINTER_NOT_ISO3166_ALPHA2, "*ZG*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "ZH", GS1_LINTER_NOT_ISO3166_ALPHA2, "*ZH*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "ZI", GS1_LINTER_NOT_ISO3166_ALPHA2, "*ZI*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "ZJ", GS1_LINTER_NOT_ISO3166_ALPHA2, "*ZJ*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "ZK", GS1_LINTER_NOT_ISO3166_ALPHA2, "*ZK*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "ZL", GS1_LINTER_NOT_ISO3166_ALPHA2, "*ZL*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "ZM");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "ZN", GS1_LINTER_NOT_ISO3166_ALPHA2, "*ZN*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "ZO", GS1_LINTER_NOT_ISO3166_ALPHA2, "*ZO*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "ZP", GS1_LINTER_NOT_ISO3166_ALPHA2, "*ZP*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "ZQ", GS1_LINTER_NOT_ISO3166_ALPHA2, "*ZQ*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "ZR", GS1_LINTER_NOT_ISO3166_ALPHA2, "*ZR*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "ZS", GS1_LINTER_NOT_ISO3166_ALPHA2, "*ZS*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "ZT", GS1_LINTER_NOT_ISO3166_ALPHA2, "*ZT*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "ZU", GS1_LINTER_NOT_ISO3166_ALPHA2, "*ZU*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "ZV", GS1_LINTER_NOT_ISO3166_ALPHA2, "*ZV*");
	UNIT_TEST_PASS(gs1_lint_iso3166alpha2, "ZW");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "ZX", GS1_LINTER_NOT_ISO3166_ALPHA2, "*ZX*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "ZY", GS1_LINTER_NOT_ISO3166_ALPHA2, "*ZY*");
	UNIT_TEST_FAIL(gs1_lint_iso3166alpha2, "ZZ", GS1_LINTER_NOT_ISO3166_ALPHA2, "*ZZ*");

}

#endif  /* UNIT_TESTS */
