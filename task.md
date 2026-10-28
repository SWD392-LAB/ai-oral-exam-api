# TASK-K — Khang: F1 question bank, FE F1/F7/F6 và kiểm thử tích hợp

## 1. Mục tiêu

Hoàn thiện phần ngân hàng câu hỏi F1 do Khang sở hữu, nối các màn hình question bank, cấu hình kỳ thi và báo cáo với API thật, đồng thời bổ sung kiểm thử contract/E2E và CI cho các luồng tích hợp.

Phân công này **không có deadline**. Thứ tự bên dưới là thứ tự phụ thuộc kỹ thuật, không phải cam kết ngày hoàn thành.

## 2. Phạm vi sở hữu

### Khang thực hiện

- Backend AccessConfig cho question bank: tạo/sửa Draft, quản lý revision, approve/reject, attach câu đã duyệt vào session Draft và import JSON.
- Backend-side contract/unit/integration tests và CI cho những luồng được phân công.
- Đề xuất schema question bank và contract attach để Minh tích hợp vào migration/publish.
- Frontend requirements are listed separately in Addendum A for the frontend owner; no frontend files are part of this backend task.

### Phối hợp nhưng không nhận quyền sở hữu

- **Minh** sở hữu `database/`, migration/schema, rubric nền tảng, publish command và contract F7 liên quan.
- **Phúc** sở hữu backend sinh câu hỏi bằng AI, ingest/RAG và provider AI. Khang nối giao diện question bank với contract generation sau khi hai bên thống nhất.
- Khang kiểm thử luồng F3/F6 đã tích hợp; lỗi nghiệp vụ F3 chuyển cho Minh hoặc Phúc theo module sở hữu. Khang không triển khai nghiệp vụ F3 thay họ.

### Không thuộc phạm vi

- Không sửa frontend trong repo `ai-oral-exam-web`.
- Không sửa file SQL trong `database/`, không tự áp dụng migration và không sửa publish command.
- Không tạo, sửa hoặc import rubric/criteria cho từng câu; không sở hữu platform rubric/version.
- Không tự triển khai AI generation, RAG, voice/STT/TTS, chấm điểm, review/lifecycle attempt hoặc SignalR của F3.
- Không thay đổi auth/đăng ký/Google SSO nếu chưa thống nhất phạm vi với Minh.
- Không tiếp tục checklist cũ B5/B6/C7/D10–D12 trong task này; chúng không nằm trong TASK-K của work plan hiện tại.

## 3. Quy tắc nghiệp vụ bắt buộc

### Question bank và revision

1. Mỗi bank item thuộc đúng một course và có nội dung câu hỏi, topic, mức Bloom, origin, trạng thái và thông tin actor/timestamps theo thiết kế đã thống nhất.
2. Các mức Bloom được hỗ trợ: `Remember`, `Understand`, `Apply`, `Analyze`.
3. Trạng thái item: `Draft`, `Approved`, `Rejected`, `Archived`. Câu mới tạo, import hoặc sinh bằng AI bắt đầu ở `Draft`; không có luồng tự động approve.
4. Lịch sử nội dung được lưu thành revision bất biến kèm provenance. Không ghi đè revision đã tồn tại; nội dung đã được dùng trong session đã publish không được thay đổi khi bank item được sửa, archive hoặc có revision mới.
5. Bank item/revision **không chứa rubric hoặc criteria**. Không nhận điểm hay criteria trong request tạo, sửa, import hoặc generation.
6. Lecturer chỉ thao tác với course mà họ được phân công, theo authorization hiện có. Không cho phép truy cập hoặc attach item từ course khác. Không mở rộng quyền Administrator ngoài chính sách hiện có nếu chưa được thống nhất.
7. Ghi audit theo pattern hiện có cho thao tác tạo/sửa/approve/reject/attach; không ghi tài liệu private, token, secret hoặc nội dung nhạy cảm vào log.

### Attach vào session

1. Chỉ attach item `Approved`, thuộc cùng course với session và khi session còn `Draft`.
2. Request phải được kiểm tra quyền lecturer đối với course/session; không tin `courseId`, trạng thái hoặc quyền do client tự khai báo.
3. Khi attach, copy nội dung câu hỏi vào session và ghi provenance tới bank revision nguồn nếu contract/schema đã thống nhất. Không copy rubric từ bank.
4. Câu hỏi và revision đã được dùng trong session publish là snapshot; thay đổi bank sau đó không làm đổi nội dung, rubric hoặc kết quả lịch sử của session.
5. Quy tắc xử lý attach trùng và việc sửa/xóa câu đã attach khi session vẫn Draft phải được thống nhất với Minh trước khi chốt API. Không tự chọn một hành vi có thể gây mất dữ liệu hoặc thay đổi bài thi.

### Import JSON

1. Import tạo bank Draft, không import trực tiếp vào session; không nhận rubric/criteria và không approve/attach tự động.
2. Validate toàn bộ batch trước khi lưu. Nếu có bất kỳ dòng không hợp lệ, không lưu một phần batch.
3. Phản hồi lỗi phải chỉ rõ số dòng và trường/cột lỗi, đủ để người dùng sửa và gửi lại.
4. Validate ít nhất: dữ liệu bắt buộc, nội dung rỗng, Bloom không hợp lệ, quyền trên course và câu trùng trong batch/bank.
5. Chốt định dạng JSON, giới hạn batch và quy tắc nhận diện câu trùng với Minh/tech lead trước khi triển khai. Nếu chưa có quyết định, ghi rõ quyết định còn thiếu thay vì âm thầm tự đặt chính sách.

### Rubric trong exam-config (F7)

1. Giảng viên không nhập hoặc sửa rubric theo từng câu. Màn hình cấu hình chỉ hiển thị rubric platform dùng chung dưới dạng read-only theo contract của Minh.
2. Session mới dùng cùng một rubric version đã pin khi publish; FE không tự chọn rubric version và không thay thế logic publish của backend.
3. F7 chỉ cho chọn câu Approved, cùng course, để thêm vào session Draft. API phải từ chối câu Draft/Rejected/Archived, câu khác course hoặc session không còn Draft.
4. Giữ nguyên các hành vi và quyền cấu hình session hiện tại ngoài phần thay đổi cần thiết để dùng question bank và rubric read-only.

### Reporting (F6)

1. Sinh viên chỉ xem report của chính attempt mình. Khi attempt đang `PendingReview`, chỉ hiển thị metadata; không tiết lộ điểm, hội thoại hoặc feedback chi tiết trước khi giảng viên xác nhận.
2. Sau khi điểm được xác nhận (`Finalized`), sinh viên mới thấy report/feedback được phép.
3. Lecturer/Admin xem kết quả, statistics và tải CSV theo quyền hiện có. Statistics dùng kết quả đã xác nhận; CSV để trống final score và thể hiện `PendingReview` cho attempt chưa xác nhận.
4. FE phải dùng API Reporting hiện có, không tự tính lại hoặc suy diễn điểm từ transcript/đề xuất AI.

## 4. Các đầu việc

### K1 — Chốt contract và phối hợp

- [x] Đối chiếu API hiện có: câu hỏi hiện tại được CRUD/import trực tiếp theo exam session; API Reporting đã có report cá nhân, results, statistics và grade-sheet CSV. Các route question bank chưa tồn tại.
- [x] Recheck Minh's newer rubric work: `database/01_schema.sql` and `database/06_rubric_versions.sql` now define rubric templates/versions and session rubric pinning; AccessConfig also has rubric template/version services and HTTP tests. This completes the rubric-schema portion only.
- [ ] Gửi Minh đề xuất/chốt schema bank/revision và contract attach; Minh tích hợp schema vào migration và nối vào publish. Chưa thể hoàn tất API lưu trữ trước khi nhận contract/schema này. Không cùng sửa `database/` hoặc publish command.
- [ ] Thống nhất với Phúc contract generation, provenance/citation và trạng thái Draft; generation backend thuộc Phúc.
- [ ] Chốt các quyết định đang mở: định dạng import/giới hạn batch, cách nhận diện câu trùng, xử lý attach trùng, chỉnh item sau approve và cách xử lý câu đã attach vào session Draft.
- [ ] Ghi rõ các API/contract còn thiếu và người phụ trách; không thay bằng mock hoặc giả định không được thống nhất.

### K2 — Backend question bank và import (F1-A2/B)

- [x] Chuẩn bị draft request contract `QuestionBankImportRequest` / `QuestionBankImportItemRequest` và validator/test độc lập với database. Validator yêu cầu content/topic/Bloom và chấp nhận đúng bốn mức Bloom. Lỗi chỉ vị trí theo JSON array path (zero-based), ví dụ `items[0].content`.
- [x] Recheck current schema and routes: no `question_bank_items`, `question_bank_revisions`, or `source_bank_revision_id` are defined in `01_schema.sql` or the available upgrade scripts; no bank service/controller or `/questions/from-bank` endpoint is present. The F1 plan documents these as proposed, not implemented.
- [ ] **Blocked — Minh cung cấp schema/contract trước:** xây dựng API theo course cho list/detail, tạo Draft, sửa nội dung Draft/revision, approve và reject. Các route dưới đây là route dự kiến theo plan F1; xác nhận route cuối cùng trước khi công bố:
  - `/api/courses/{courseId}/question-bank`
  - `/api/courses/{courseId}/question-bank/{itemId}`
  - `/api/courses/{courseId}/question-bank/{itemId}/approve`
  - `/api/courses/{courseId}/question-bank/{itemId}/reject`
- [ ] **Blocked — Minh cung cấp attach/schema contract trước:** tạo endpoint attach câu Approved vào session Draft, dự kiến `/api/exam-sessions/{sessionId}/questions/from-bank`. A route proposal in the F1 plan is not an implemented API contract; there is no request/response DTO, service, controller or persistence mapping yet.
- [ ] **Blocked — schema và quyết định import còn thiếu:** tạo endpoint import JSON vào bank, dự kiến `/api/courses/{courseId}/question-bank/import`; validate toàn batch và chỉ persist toàn bộ khi tất cả dòng hợp lệ.
- [ ] Khi storage API được triển khai, kiểm tra quyền course, trạng thái bank/session, revision/provenance và snapshot; đảm bảo không thể truy cập chéo course hoặc thay đổi nội dung session đã publish.
- [ ] Khi endpoint sẵn sàng, bổ sung HTTP/contract tests cho quyền, trạng thái, revision, attach và atomic import. Test hiện tại chỉ bao phủ validation thuần, chưa chứng minh persistence/atomicity hay API behavior.

### K3 — Frontend work

Frontend tasks are moved to **Addendum A** because this repository contains only the backend. Do not modify the frontend repository as part of this backend task.

### K6 — API mapping, kiểm thử tích hợp và CI

- [ ] **Blocked in part:** lập bảng route/request/response/error đầy đủ sau khi bank, attach and generation contracts được chốt. Reporting routes were verified; rubric template/version APIs now exist in backend. FE mapping remains in Addendum A.
- [x] Thêm backend GitHub Actions CI chạy restore và `dotnet test AiOralExam.sln` trên .NET 8 cho push, pull request và workflow dispatch.
- [ ] **Blocked:** E2E với PostgreSQL/Keycloak và luồng question bank → publish → F3 → review → F6 cần schema/migration và hợp đồng Minh/Phúc. Không chạy trên database dùng chung.
- [ ] Duy trì fake AI/storage trong CI khi các adapter được tích hợp; smoke test provider thật là bước riêng, không đưa credential, transcript hoặc tài liệu private vào CI/log.
- [ ] Sau khi bank API sẵn sàng, bổ sung negative/regression tests cho sai quyền/course, trạng thái item/session, duplicate policy, atomic import và concurrency. Chuyển lỗi nghiệp vụ F3 cho Minh/Phúc theo ownership.
- [x] Full solution unit/integration tests were run locally; results are recorded below. Tests currently use the repository's in-memory test setup and do not count as PostgreSQL/Keycloak E2E.

## 5. Thứ tự phụ thuộc và bàn giao

1. Minh chốt/cung cấp bank/revision/attach schema contract; Minh sở hữu schema migration và publish integration.
2. Chốt generation/citation contract với Phúc; chuyển FE contract cho frontend owner, không giả định API generation đã tồn tại.
3. Hoàn thiện BE bank/import, attach và PostgreSQL contract tests sau khi schema/contract sẵn sàng.
4. Chuyển giao API contract cho frontend owner; frontend tiếp tục các mục tại Addendum A.
5. Bật E2E PostgreSQL/Keycloak cho các luồng đã tích hợp; ghi rõ phần nào còn chờ Minh hoặc Phúc.

Mỗi thay đổi API cần được thông báo cho FE/BE liên quan. Không yêu cầu người khác chờ toàn bộ TASK-K mới bắt đầu phần độc lập, nhưng không merge dựa trên contract chưa thống nhất.

## 6. Tiêu chí nghiệm thu (Definition of Done)

- [ ] Storage-backed bank API, workflow revision/moderation, attach rules and atomic JSON persistence pass contract tests. **Blocked on Minh's schema/contract.**
- [ ] Only Approved items for the same course attach to Draft sessions; published question snapshots remain unchanged. **Blocked on schema/attach integration.**
- [x] The import-validation contract rejects absent content/topic and unsupported Bloom levels; all four supported Bloom values have unit coverage.
- [x] Existing backend F6 API enforces the report visibility and role rules in its service/tests; no backend change was needed for this part. Frontend consumption remains in Addendum A.
- [x] Backend CI workflow is added; local full-solution tests pass. PostgreSQL/Keycloak E2E remains open.
- [x] No changes to SQL/publish command or Minh/Phúc-owned F3 implementation.

## 7. Implementation status and blockers

- The existing question CRUD/import API is session-scoped and does **not** implement a question bank. It remains unchanged; do not treat it as satisfying F1-A2/B.
- Added a provisional import request contract and pure validator. This validates non-empty batch, row presence, content/topic and the four Bloom values. It does not decide duplicate rules, persist data, authorize a course, or provide atomicity because those require the agreed schema and endpoint.
- Minh's newer rubric schema/API work is present: the fresh-install schema and forward-only upgrade now cover shared rubric templates/versions and session pinning, with AccessConfig services/controllers and tests. This does **not** provide the question-bank schema.
- F1 bank list/detail/create/revision/approve/reject, bank import endpoint, attach endpoint, audit writes and snapshot persistence are **still blocked**. `question_bank_items`, `question_bank_revisions`, and source-revision linkage are absent from available SQL; no bank API or attach endpoint exists. The proposed attach path in the F1 plan is documentation only. The task ownership rule prohibits Khang from editing `database/` or publish.
- Still to decide with Minh/tech lead: JSON shape and batch limit, duplicate definition, attach-duplicate behavior, editing/rejecting after approval, and handling attached questions while a session remains Draft.
- Next-session prerequisite: Minh supplies/merges the bank and revision schema plus the attach contract (including source revision linkage and duplicate/ordering behavior). Then implement the service/controller/EF mapping, transactional import, authorization and audit, and exercise the attach/publish snapshot path. Do not start persistence against the current schema.
- The F6 backend endpoints and service are already present; existing tests cover pending-report privacy, student ownership, lecturer assignment, confirmed grade CSV, and statistics/results authorization. FE is not present in this repository.
- PostgreSQL/Keycloak end-to-end validation was not run. Existing integration tests replace both module databases with EF InMemory and use fake authentication/Keycloak. Two PostgreSQL rubric concurrency tests are skipped unless the test database environment is configured.
- The current build emits a duplicate `using` warning in `RubricTemplatesController.cs` and two xUnit analyzer warnings in `KeycloakAdminClientTests.cs`; they are outside Khang's assigned changes and were not altered.

## Addendum A — Frontend work for `ai-oral-exam-web`

These are frontend-only tasks; complete them in the frontend repository after the backend contracts are agreed and the required APIs are available.

### Question bank (F1)

- Implement the shared API client and question-bank list/detail/create/edit screens using real bank endpoints; show topic, Bloom level, origin, state, revision and provenance.
- Add JSON import UI. Show row/field errors clearly and show success only when the complete batch was accepted.
- Show AI-generated Drafts and source citations when Phúc's generation API provides them; let lecturers edit/review/approve/reject according to the agreed state transitions.
- Allow selecting only Approved questions for a matching Draft session; surface backend errors. Do not add per-question rubric inputs.
- Use `VITE_USE_MOCKS=false` for the integrated-flow verification.

### Exam configuration (F7)

- Select approved bank items instead of creating questions directly in the session.
- Remove per-question rubric editing; display the platform rubric and criteria read-only from Minh's contract.
- Preserve existing session configuration behavior; do not claim attach/publish succeeded when the API returns an error.

### Reporting (F6)

- Connect student attempt report, lecturer results/statistics and CSV download to existing Reporting endpoints.
- For student attempts before `Finalized`, show only permitted metadata and pending-review state; hide scores, dialogue and detailed feedback.
- Show permitted stored report details after final confirmation. Never expose an unconfirmed AI score.
- Handle loading, empty, forbidden/not-found and API error states; verify that pending grades remain blank in the downloaded grade sheet.

### Frontend integration verification

- Update the frontend route/request/response/error mapping when backend contracts are finalized.
- Run FE contract/E2E flows against the integrated backend and Keycloak; include unauthorized/cross-course cases and record any backend defect for its owning developer.
- Frontend build/test and `VITE_USE_MOCKS=false` checks are outside this backend repository and have not been run here.

## 8. Verification run

- Focused import validator unit tests: **10 passed, 0 failed** (previous run).
- Latest full `dotnet test AiOralExam.sln --no-restore`: **225 unit tests passed, 2 PostgreSQL concurrency tests skipped, and 31 integration tests passed**.
- Latest suite build emitted one duplicate-using warning in `RubricTemplatesController.cs` and two xUnit analyzer warnings in `KeycloakAdminClientTests.cs`; tests still passed. No edits were made to those files.
- The backend CI workflow is configured to run restore and Release solution tests on push, pull request, and workflow dispatch; its GitHub-hosted result cannot be observed from this workspace.

## 7. Tài liệu tham chiếu

- Phân công tổng thể: [Project-Work-Plan-Minh-Phuc-Khang.md](./Project-Work-Plan-Minh-Phuc-Khang.md)
- Thiết kế F1, contract dự kiến và rule import/bank: [docs/F1-Implementation-Plan.md](./docs/F1-Implementation-Plan.md)
- Hợp đồng và luồng F3/F6 liên quan đến kiểm thử tích hợp: [docs/F3-Implementation-Plan.md](./docs/F3-Implementation-Plan.md)
- API Reporting hiện có: `src/Modules/Reporting/AiOralExam.Modules.Reporting/Controllers/ReportsController.cs`
