# Mediation Agreement Auditor

Blank 0-1 baseline for a single-file C# agreement consistency auditor. The repository includes fixtures but no starter implementation.

## Source material

`materials/agreements.json` fixes party IDs, clause IDs, amendment links, signatures, amounts, and evidence paths.

## Required delivery contract

- Create the functional implementation in one C# source file; project metadata and tests do not count toward that one-file domain boundary.
- Stable issue codes use the `MED_` prefix and public APIs include XML documentation.
- Native verification: `dotnet test` and `dotnet run`.
- Final response must map issue codes to fixtures and test names in a Markdown table.

Docker is not required.

