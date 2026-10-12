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
 * @file lint_iso3166999.c
 *
 * @brief The `iso3166999` linter ensures that the data represents an ISO 3166 "num-3" country code, or the value "999".
 *
 * @remark The three-digit country codes are defined by [ISO 3166-1:2020: Codes for the representation of names of countries and their subdivisions - Part 1: Country code](https://www.iso.org/standard/72482.html) as the "num-3" codes.
 *
 */


#include <assert.h>
#include <string.h>

#include "gs1syntaxdictionary.h"
#include "gs1syntaxdictionary-utils.h"


/**
 * Used to validate that an AI component is an ISO 3166 "num-3" country code or
 * the string "999".
 *
 * @param [in] data Pointer to the data to be linted. Must not be `NULL`.
 * @param [in] data_len Length of the data to be linted.
 * @param [out] err_pos To facilitate error highlighting, the start position of
 *                      the bad data is written to this pointer, if not `NULL`.
 * @param [out] err_len The length of the bad data is written to this pointer, if
 *                      not `NULL`.
 *
 * @return #GS1_LINTER_OK if okay.
 * @return #GS1_LINTER_NOT_ISO3166_OR_999 if the data is not a num-3
 *         country code or the string "999".
 *
 */
GS1_SYNTAX_DICTIONARY_API gs1_lint_err_t gs1_lint_iso3166999(const char* const data, size_t data_len, size_t* const err_pos, size_t* const err_len)
{

	gs1_lint_err_t ret;

	assert(data);

	/*
	 * The data may contain the string "999".
	 *
	 */
	if (data_len == 3 && data[0] == '9' && data[1] == '9' && data[2] == '9')
		GS1_LINTER_RETURN_OK;

	/*
	 * Validate the data with the iso3166 linter.
	 *
	 */
	ret = gs1_lint_iso3166(data, data_len, err_pos, err_len);

	assert(ret == GS1_LINTER_OK || ret == GS1_LINTER_NOT_ISO3166);

	if (GS1_LINTER_LIKELY(ret == GS1_LINTER_OK))
		GS1_LINTER_RETURN_OK;

	GS1_LINTER_RETURN_ERROR(
		GS1_LINTER_NOT_ISO3166_OR_999,
		*err_pos,
		*err_len
	);

}


#ifdef UNIT_TESTS

#include "unittest.h"

void test_lint_iso3166999(void)
{

	UNIT_TEST_PASS(gs1_lint_iso3166999, "999");

	UNIT_TEST_PASS(gs1_lint_iso3166999, "004");
	UNIT_TEST_PASS(gs1_lint_iso3166999, "894");

	UNIT_TEST_FAIL(gs1_lint_iso3166999, "000", GS1_LINTER_NOT_ISO3166_OR_999, "*000*");
	UNIT_TEST_FAIL(gs1_lint_iso3166999, "998", GS1_LINTER_NOT_ISO3166_OR_999, "*998*");

	UNIT_TEST_FAIL(gs1_lint_iso3166999, "", GS1_LINTER_NOT_ISO3166_OR_999, "**");
	UNIT_TEST_FAIL(gs1_lint_iso3166999, "9", GS1_LINTER_NOT_ISO3166_OR_999, "*9*");
	UNIT_TEST_FAIL(gs1_lint_iso3166999, "99", GS1_LINTER_NOT_ISO3166_OR_999, "*99*");
	UNIT_TEST_FAIL(gs1_lint_iso3166999, "9999", GS1_LINTER_NOT_ISO3166_OR_999, "*9999*");
	UNIT_TEST_FAIL(gs1_lint_iso3166999, "_999", GS1_LINTER_NOT_ISO3166_OR_999, "*_999*");
	UNIT_TEST_FAIL(gs1_lint_iso3166999, "999_", GS1_LINTER_NOT_ISO3166_OR_999, "*999_*");
	UNIT_TEST_FAIL(gs1_lint_iso3166999, "AAA", GS1_LINTER_NOT_ISO3166_OR_999, "*AAA*");

}

#endif  /* UNIT_TESTS */
