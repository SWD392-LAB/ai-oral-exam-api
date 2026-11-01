# F1 platform rubric lifecycle

- A SharedVersion session must use an active, sealed, platform-owned rubric when it is first published. Activating a later version does not change pins on published sessions; those sessions can still be updated in permitted non-rubric fields or closed.
- A Draft remains pinned to the rubric version selected at creation. If that version is deactivated, ordinary Draft edits with the same pin remain allowed, but publication is rejected until the Draft is pinned to the current active version.
- The current API has no rubric-reselection endpoint. The database trigger permits changing a Draft pin only to an active, sealed platform version and only before questions have been added. Otherwise, create a replacement Draft using the active version and recreate its questions and participants; keep the old Draft until the replacement is ready, then remove it through the normal Draft deletion workflow if appropriate.
- Published/Closed session pins are immutable. Do not rotate a platform version by editing existing session rows.

Activation and initial SharedVersion selection/publication use the same transaction advisory lock as `RubricVersionService.ActivateAsync`. This keeps the active check serialized with rotation. Trigger functions capture the selected application schema in their `search_path`; they remain invoker-security functions, so the application role still needs its normal table privileges.
