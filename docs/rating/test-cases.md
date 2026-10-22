# Rating test cases

The rating engine (ADR-007) has two kinds of test case. They are kept apart.

## Engineering cases (written so far)
Premiums worked by hand from the engine's own rules. They show the engine does what ADR-007 says: step order, banding, caps, bounds, rounding and tax. They do **not** show that the premiums are actuarially right.

- `tests/Insys.Rating.Tests/RatingEngineTests.cs`: unit tests, marked with the trait `Category=EngineeringCase`.
- `tests/Insys.IntegrationTests/SharedServicesTests.cs`: `PremiumCalculation_IsRecordedWithItsBreakdown_AndIsTenantScoped_PRD008`.
- `tests/Insys.EndToEndTests/ProductPublicationTests.cs`: case `E2E-TC-1` in the seeded test case library.

## Actuary-supplied cases (still needed)
For each product, cases from the actuaries' own rating models, loaded into `rating.cfg_rating_test_case_library` for the product version. They are approved like any Tier 1 static data. Product Factory runs them before publication (PRD-005).

Each case needs:
- inputs (with `currency` and `frequency`);
- the expected premium;
- a tolerance.

These cases, and decisions on the open points in OQ-052 (step order, cap rule, PERCENT parameters, levies on commission), come from the actuarial team. Until then, no product's rating is actuarially validated.
