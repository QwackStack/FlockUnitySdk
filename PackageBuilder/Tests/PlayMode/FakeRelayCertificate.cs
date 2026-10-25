namespace Flock.Tests.PlayMode
{
    // A self-signed certificate for relay.test, localhost and 127.0.0.1 (made with openssl on 2026-10-25, lasting 100 years) and its key,
    // for the in-memory relay's TLS side alone. No platform trusts it: a client lets it through only with a test's own certificate check.
    internal static class FakeRelayCertificate
    {
        internal const string Password = "flock-test";

        internal const string Pfx =
            "MIIJeQIBAzCCCT8GCSqGSIb3DQEHAaCCCTAEggksMIIJKDCCA98GCSqGSIb3DQEHBqCCA9AwggPMAgEAMIIDxQYJKoZIhvcNAQcBMBwGCiqGSIb3DQEMAQMw"
            + "DgQIMnFamwv88SUCAggAgIIDmPEynbIhFyFksmYt8+HMgLIow0ew+Ql8Oqe/HdDEVDE1lpELg9MGsGbUQm7CRVtgeGrhJ/E4u/vfGkVgWtgJZYwAfM146zx3"
            + "zdlZty8kgWeIz/jeAS+HA30tSIbTEIFMObfhvaq217xu4/T2OzPmEXOnLCcYksqDa1LjJwvlz4uQGTcrS+5A74qu0jSu62O0u2kMKHPNEJc3k10/vlPM0/qS"
            + "dBEXmEuhVhoyAfSPgjSWJAPby7uj3E2QibkentGm79fqVrp1kb3vr/f5uGDwY/2sshWME4pfV4hhfJ9RHdBCmbhhBt62jBgf2/LSWj2jAWwv2OHV6RupuiAr"
            + "7rCh97DqMCjOJVLjEFSxK+0DFSwNg1slqAhJKWuKvbSGEZC0l5bFFi8wNH2rxkOc4AdZu0cvXDUnQmMh5xB8pdkeH7Q3e5vY0O8tv03zLrxzpvncEFELZvBk"
            + "ePVLDAwDDwPg3ErmjRY9hXMYh92uZafscO4APKQ/F78XVdZQycN7JrNkKHzpIuqkTxpYL2VLpURcEyB1XW83Rd2SFfyXWByMhboi7xUhTszvzI08QuG9eAPR"
            + "cRYlPDh/j+9xPQMR7nXJ6TL6EM1tzxuOiWpcDJ5KrXiphRw+RYcXf8N2YJRb6w4YvfBOBQ84J3PB7HAtc5KtAiOHuAASHBl6pWv+xaL+Z/5T++qrNYkY9Z8p"
            + "hNcQFKClOl4A9OvlE5+ftFvS78PN/R27rhmvSjlKkIdV5r/QlYhGtDhaHYKXHe8NnJbtp/gG74BEB8H+284IblJrEXz03ealYvJdx6arIUA0Mz+Z9fxouXc5"
            + "vcibT5s5Fiv/gRekhVXJcmjXJ954GQ3SUpdBZeTYmL8uOp51WrHxt/R+CPoRvmBDlpVBYnHbB75bHRtYWJzuy6kUQ3HT3YgwIpyiT8y+gkDZbpOCOx53hZcL"
            + "rbs3evQqoAD1w+fOHBQZG32q1bFWxhocDFh/pr66hihJ5uv7IyYC6XCEc6zZQGft4jxDTGn+bvvhGXhwCtVRBii9VcWKkU75o4894Bq1HLCo1iRjUGI3P6DG"
            + "bjMmlhyAmNQA8jPkaA5FrjkBoBJNoKvYgzvpohvZxeuOu0Y+knkr8Ni6rfzvLCOrKXOcIfD536BpuX4fu4Q95RVdNardFjPSQR2R9kyZ8dbN2jyEnutEwtT3"
            + "KpsD1qF46N8zqcS+axckZn8qcGJijewkJIhuiSXIhWTOvyFjeiPaMIIFQQYJKoZIhvcNAQcBoIIFMgSCBS4wggUqMIIFJgYLKoZIhvcNAQwKAQKgggTuMIIE"
            + "6jAcBgoqhkiG9w0BDAEDMA4ECGnQOHkETwvqAgIIAASCBMh91PMRGKTKNJjS8FLECXIbZJ1WZ1zmuzgep4lPeRWisGRAdgh4+hi/GEFubv1wvBa23XwdkxJA"
            + "l/DKzYrqdbx509siSuVsptcDLxxtAyz9M38g0uVz0id0uBAEUymBLvUOlXUZDsvUOoVzRRAeb3SbqSgW038q1LRxfNT86WAKtrdmKD7y96OZuYR4zukiuLGN"
            + "CNgLsupZ1sqqQG5kjMYzX700saxFPKmptPucBSYTOWNkBFwsJ2VZZakUlDQVokvmMtWh9683vXbWkbmmKc6zUcK53Urrp7s2r8xK3VikZWOVeP/RsIvYII4n"
            + "VaSB5oWetjPm/5Nuj8+Eg6vPViQgigST3Je6Jpjn3CRnYl0Rbd37o6XwzeWVA1/hQyN4X08qxjjcBKH+hTHWRvfq3a1cPlMH9o2ejrezBz3IfgKeibOL1DYP"
            + "BlOP18RmQmGiRM2xGsshEAhMZTFKOw7fn9zxItRpxUpyRWtFy5CT3CkDVaNnFa5/Jw8p31/3NlPfaH7po8MNLB/nnwsdd2KB24EF0Rk/AjjwRGzWVkbmzd0F"
            + "nzUVcfsC9xHZzSsf67A5ODASB7lECt09ry5ecib0vdKWCVkqE5tJ240a2mqU9da4cBOvnUboaJBB0BimuXaMC8j0QzHNqx5vjPHNRIKroivyffPISbnLSrnv"
            + "3P36Egb0FZuDlMoafWr0fwjROsIoMXY8Npa2H8JeSgNrcwTnQDlHPHW5zDzsws7P2tjdq9g9walMu/CPzh8rniX3TgsyMY9bGpDGU8vOvaRfh46ZjGGR4tH/"
            + "ZqM2OV1XPweVkJpzy3ll8EqhpoXSR2T989plTfbjyz0rNV+7vLog3WwIZuyx3ZdbhIZQ4L9Wjr4O9zd8OWJAjG1ybLvbwLZ4U87VQM88M5anvN5dM/YGbcvL"
            + "xWPkCQbYH6s5SMlXCXWdlKBcS1ysSC2rdVjiAXtWNWxAYEY/PqxayBMpQklVqT7XIjrKVTn6ou9Z7zdgWttAXesayiqbCqlsUeZqoygsezucFfncfLCeCis2"
            + "hCRUnvwtLYeehzNPAVemglWzyUIG+XT9XmcaedZRr0LKwAeqKNWatwY9MnKTQH7YzjMuC6uNQMD3f8DRtGJsR+kUZB7hzn5gwvSCjpk/z5LKisJos3El5eUo"
            + "I8OYdNrTzSe1WJE+h3T/ZLkh+I1299fd2Ngoe/mUOZz3AO5nJQ8uF85NDLeFcElqZiu8hnRkkKgoeBgeQB1za13fJg4nnGdChZC9uIRWGZPRytwPVM/wBX/l"
            + "PGsimE2oTd1I2AjWvesm21NcTVhrxrPM1JUJVzVI4PS8Vy3M9utYpzGBE6zDjwmkeA6ObqCDP5k9VjpLslY2qEGOYt+AhCXQa45fVf5N/TRWDOpIC4SGfvMd"
            + "hdSBVtoYU4BdlVX6qO3rCaXW2ezPbyULTwVcxeDAmfJQ+rYYSep2a2+Ay53QxLEbM9kvDnwk9xAN9f4m2HJQUloy5mkGiVx/sL1hMslJLeOqbbgKLLI4g25A"
            + "9cFzcRonPDtQ22VNTnzJhEHIvcJCRHm3tTR+0CetxPd/AR9H9YM9vdWyH5YWR+LHwkR5feEsLIYn1uaLXz4cRlqwl7cTePe3s/FD4pi9alRAr+eYG9tKj48x"
            + "JTAjBgkqhkiG9w0BCRUxFgQUN8cpaJJLlzh3gGMYAq3Gp5D+R5MwMTAhMAkGBSsOAwIaBQAEFEijsfuX6EmATQ5kWXx+80f6F71bBAg/wx+nFbOPWAICCAA=";
    }
}
