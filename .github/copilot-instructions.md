
# GitHub Copilot Instructions

## Git Safety Rules

- Never run git commit, git push, git reset, git clean, or other destructive Git commands unless explicitly authorized by the user.
- Never automatically commit or push changes.
- Make code changes only in the working tree.
- Before finishing, summarize all modified files and verification results.
- Let the user review and approve changes before committing or pushing.

## Database Safety Rules

- Never run SQL, migrations, or database tools (psql, dotnet ef database update, docker exec/compose against the database, etc.) against any database. The user applies database changes manually.
- Treat database/01_schema.sql as the source of truth for the schema. Do not edit any .sql file unless the user explicitly asks for it in the current request.
- If code and schema disagree, report the mismatch and say which file needs to change. Do not silently change the schema to match the code.
- Never drop, truncate, or recreate tables or containers.

## 1. Mandatory Repository Inspection

Before answering project-specific questions or proposing code changes:

- Inspect the relevant files in the current repository.
- Read existing implementations before suggesting replacements or new code.
- Check related models, entities, services, interfaces, dependencies, tests, and configuration when relevant.
- For database questions, inspect `database/01_schema.sql` if it exists.
- For entity mapping questions, inspect the database schema, entity classes, mapping configuration, and related code.
- For workflow questions, inspect the relevant business logic and requirements instead of assuming CRUD operations represent complete workflows.
- For architecture questions, inspect the existing solution and project structure.
- Never claim to have read or verified a file unless you actually inspected it.
- If a required file is unavailable, explicitly state what could not be inspected. Never invent its contents.

## 2. Think Before Coding

Don't assume. Don't hide confusion. Surface tradeoffs.

Before implementing:

- State important assumptions explicitly.
- If multiple interpretations exist, explain them instead of silently choosing one.
- If a simpler approach solves the problem, recommend it.
- Push back politely when a requested approach introduces unnecessary complexity or risks.
- If essential requirements are unclear, ask for clarification before making significant changes.

## 3. Simplicity First

Write the minimum code that correctly solves the requested problem.

- Do not add features that were not requested.
- Avoid abstractions for single-use code unless they provide a clear benefit.
- Do not introduce unnecessary flexibility, configurability, or design patterns.
- Avoid unnecessary error handling for impossible or irrelevant scenarios.
- Prefer straightforward, readable implementations.
- Follow existing project conventions unless there is a concrete reason to change them.
- Do not overengineer simple requirements.

Ask: Would a senior engineer consider this implementation unnecessarily complicated? If so, simplify it.

## 4. Surgical Changes

Touch only what is necessary.

When editing existing code:

- Modify only code directly related to the request.
- Do not refactor unrelated code, comments, formatting, or architecture.
- Preserve existing behavior unless the request requires changing it.
- Do not rename classes, methods, properties, or files without a clear reason.
- Do not create duplicate classes, methods, or functionality that already exist.
- Do not delete pre-existing code merely because it appears unused.
- Remove unused imports or variables introduced by your own changes.
- Explain unrelated issues you discover without fixing them unless requested.

Every change should be traceable to the user's request.

## 5. Goal-Driven Execution and Verification

Define clear, verifiable success criteria for implementation tasks.

For multi-step tasks:

1. Identify the relevant files and requirements.
2. Inspect the existing implementation.
3. State a brief plan and success criteria.
4. Make the smallest necessary changes.
5. Run relevant tests or build commands when available.
6. Inspect the results and fix issues caused by the changes.
7. Report what changed and what was actually verified.

Examples:

- Bug fix: reproduce the issue when possible, fix it, and verify the behavior.
- Validation: test valid and invalid inputs.
- Refactoring: preserve behavior and run relevant tests.
- Database changes: verify schema consistency and related entity mappings.

Never claim that tests, builds, or commands succeeded unless they were actually executed and their results were checked.

If verification cannot be performed, explain why and provide the remaining verification steps.

## 6. C# and .NET Guidelines

When working with C#/.NET code:

- Follow the target framework and existing project configuration.
- Use appropriate C# naming conventions.
- Respect the existing architecture and separation of responsibilities.
- Keep business logic out of unrelated presentation or infrastructure code.
- Use asynchronous APIs where appropriate, following existing conventions.
- Handle nullable values and exceptions according to the actual requirements.
- Avoid adding NuGet packages unless necessary.
- Check existing dependencies before introducing new ones.
- Do not assume a class, property, method, or package exists without checking the repository.

## 7. Database and Entity Mapping Guidelines

For database-related tasks:

- Inspect the actual SQL schema before making schema-dependent claims.
- Verify table names, column names, data types, primary keys, foreign keys, constraints, and relationships.
- Compare entity properties with the actual database schema.
- Inspect existing ORM configuration and mapping code when applicable.
- Do not invent database columns, entities, relationships, or business rules.
- Identify mismatches between schema and application code explicitly.
- Avoid changing the database schema unless the task requires it.
- Consider the impact of database changes on existing code and tests.

## 8. Code Review Guidelines

When reviewing code:

- Prioritize correctness, bugs, security, data integrity, and maintainability.
- Report actionable findings supported by inspected code.
- Explain the impact and location of each significant issue.
- Distinguish confirmed bugs from potential risks and style suggestions.
- Avoid reporting personal preferences as defects.
- Do not rewrite the entire implementation when a focused correction is sufficient.
- If no significant issue is found, state the scope of the review and any limitations.

## 9. Response Guidelines

For project-specific questions:

- Explain the reasoning behind important recommendations.
- Reference relevant file paths, classes, methods, or configuration.
- Clearly distinguish verified facts, assumptions, and suggestions.
- Prefer concrete examples from the current project over generic examples.
- For complex changes, explain the plan before implementation.
- Do not claim repository access, file inspection, or execution results that did not occur.
- If information is insufficient, ask for the missing information instead of guessing.

## 10. Scope and Priority

- Follow the user's explicit requirements and the repository's existing conventions.
- Treat these instructions as default guidance, not permission to make unrequested changes.
- For trivial tasks, avoid unnecessary planning and explanations.
- For complex or risky tasks, prioritize correctness, minimal changes, and verification over speed.
- If a specific user requirement conflicts with a guideline, identify the conflict and explain the tradeoff before proceeding when necessary.