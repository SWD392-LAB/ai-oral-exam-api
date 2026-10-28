# Plan triển khai BE Feature 1 — Ngân hàng câu hỏi và rubric nền tảng

Tài liệu này ghi yêu cầu và thiết kế F1, sau đó cần đối chiếu với implementation status bên dưới; các route hoặc migration được mô tả là "dự kiến" trong phần thiết kế không đồng nghĩa với việc chưa có trong repo.

**Implementation status:** repository hiện có schema bank/revision và source-revision link (`database/01_schema.sql`, `database/07_question_bank.sql`), course-scoped bank APIs, import, moderation, attach, private course material upload/ingest/retrieval, deterministic generation và citation provenance. Platform rubric templates/versions, session pinning, AccessConfig APIs và publish integration cũng đã có schema/code. Frontend read-only rubric and question-bank flows remain deferred to `ai-oral-exam-web`. The current AccessConfig module build passes; question-bank unit/HTTP contract tests and the complete PostgreSQL/Keycloak flow still need verification after unrelated F3 compile errors are resolved.

Sequence diagram: [F1 – AI Question Generation and Review](SequenceDiagram-F1-Question-Bank-RAG.drawio). Luồng cấu hình và publish phiên dùng [diagram F7](SequenceDiagram-F7-Exam-Configuration.drawio).

## 1. Yêu cầu đã chốt theo feedback

Hệ thống có **một rubric dùng chung do platform sở hữu**, tồn tại trước khi tạo câu hỏi và áp dụng cho mọi môn/câu. Giảng viên không tạo, sửa, import tiêu chí hoặc điểm rubric. Bản rubric hiệu lực được seed hoặc administrator quản lý; mỗi lần thay đổi tạo **phiên bản bất biến** để bài thi cũ giữ nguyên cách chấm.

Giảng viên cung cấp tài liệu môn học hoặc một số câu hỏi mẫu, chọn course/chủ đề/mức Bloom (`Remember`, `Understand`, `Apply`, `Analyze`). AI dùng nội dung nguồn **và rubric chung** để đề xuất câu hỏi. Câu AI sinh là Draft; giảng viên sửa, duyệt hoặc loại bỏ trước khi đưa vào ngân hàng chính thức. Câu Approved mới được chọn cho phiên thi Draft. F3 chấm bằng phiên bản rubric đã chốt cho phiên, không đọc rubric mới nhất giữa bài thi.

F1 sở hữu ngân hàng, nguồn tài liệu, RAG và duyệt câu. AccessConfig quản lý phiên thi và rubric nền tảng; Interview (F3) chấm; Reporting (F6) hiển thị kết quả đã lưu.

## 2. Repo hiện tại và migration cần làm

| Thành phần hiện có | Sự thật trong repo | Thay đổi cần thiết |
| --- | --- | --- |
| `question_bank_items`, `question_bank_revisions`, `questions` | Bank theo course, immutable content revisions và nullable source revision link có trong `01_schema.sql`/`07_question_bank.sql`; AccessConfig mapping/service/controller triển khai bank flows | PostgreSQL upgrade, uniqueness under concurrent imports and published-session end-to-end behavior still require verification |
| `QuestionsController`, `QuestionBankController`, attach controller | Legacy questions API remains session-scoped; separate course-scoped list/detail/create/update/import/moderation and `questions/from-bank` routes exist | Frontend must switch to bank APIs for new flows; legacy behavior remains for compatibility |
| `RubricsController`, platform rubric APIs/services | Legacy per-question rubric APIs remain; shared platform rubric templates/versions and read APIs also exist | Frontend should use the read-only platform rubric for the new flow; confirm legacy write-route rollout/deprecation separately |
| `ExamSessionCommands.PublishAsync` | Platform rubric version is validated and pinned on publish for the platform-rubric flow | Verify end-to-end with PostgreSQL and F3; preserve legacy session behavior |
| `IExamConfigurationApi` and F3 | Session configuration exposes the pinned rubric criteria for platform-rubric sessions while retaining the legacy path | Run integration checks across new and historical sessions after F3 builds |
| Storage/AI | Private course material upload/ingestion/retrieval, deterministic generation and citation provenance are present | External provider smoke tests and frontend flows are not part of local verification |

Schema changes remain owned by Minh. Do not edit SQL or publish implementation as part of Khang's backend task without explicit authorization.

## 3. Rubric chung và tương thích F3

- Tạo `platform_rubric_versions` và `platform_rubric_criteria`: chỉ một version active, tiêu chí có `order_no`, `description`, `max_points > 0`; tổng điểm một câu dương, trong giới hạn kiểu điểm. Seed version đầu tiên; chỉ administrator có quyền tạo/kích hoạt version mới. Lecturer chỉ đọc qua `GET /api/platform-rubric` (route dự kiến).
- `exam_sessions.platform_rubric_version_id` được pin khi publish. Mọi câu trong cùng phiên dùng **cùng bộ criteria**. `QuestionInfo.Criteria` tiếp tục trả danh sách per question để không buộc F3 đổi giao diện contract, nhưng nguồn là version của session, không phải rubric do lecturer tạo cho từng câu.
- `QuestionInfo.MaxScore = SUM(platform_rubric_criteria.max_points)` cho từng câu; `ExamSession.TotalMaxScore = số câu × MaxScore`. Kiểm tra tổng phiên nằm trong giới hạn cột `score_reviews.final_score` hiện tại hoặc nâng kiểu cột trong cùng migration.
- `criterion_scores` hiện FK tới `rubric_criteria` cũ. Migration phải cho điểm mới tham chiếu `platform_rubric_criteria` (ví dụ cột `platform_criterion_id` và CHECK đúng một trong hai FK); điểm cũ tiếp tục đọc theo FK cũ. F3 scorer dùng ID criterion của version đã pin. Test báo cáo F6 cho cả session cũ và mới.
- Chốt phạm vi khóa route ghi rubric theo câu ở lượt rollout cùng FE. Không để lecturer ghi rubric cũ sau khi session mới đã dùng rubric nền tảng. Không xóa rubric/criterion cũ khi attempt lịch sử còn tham chiếu.

## 4. F1-A — Ngân hàng thủ công, duyệt và đưa vào phiên

- `question_bank_items` thuộc course, có topic, Bloom, content, origin (`Manual/Import/Ai`), trạng thái (`Draft/Approved/Rejected/Archived`), actor/timestamps. `question_bank_revisions` lưu phiên bản nội dung bất biến và provenance; **không có rubric/criteria trên item hoặc revision**.
- API dự kiến dưới `/api/courses/{courseId}/question-bank`: GET list/detail, POST Draft, PUT Draft revision, POST `/{itemId}/approve`, POST `/{itemId}/reject`. Body chỉ có câu hỏi, topic, Bloom và metadata nguồn; không nhận điểm/criteria.
- `POST /api/exam-sessions/{sessionId}/questions/from-bank`: kiểm tra lecturer được phân công, item Approved cùng course và session Draft; copy **nội dung câu hỏi** vào `questions`, ghi `source_bank_revision_id` nullable. Không copy rubric vào từng câu. Khi publish, F7 pin platform rubric version cho toàn phiên.
- Route tạo/import câu trực tiếp vào phiên hiện có là đường legacy; khi FE chuyển
  sang bank, khóa quyền lecturer trên đường đó hoặc chuyển request thành Draft
  bank để không bỏ qua bước duyệt/chọn câu Approved. Giữ đường đọc dữ liệu cũ.
- Câu đã publish và attempt cũ không đổi khi bank item bị sửa/archived hoặc rubric active đổi. Câu session cũ vẫn giữ dữ liệu rubric lịch sử. Quy tắc sửa bản copy trong Draft và xử lý attach trùng cần chốt trước khi code API.
- Validate content/topic, bốn mức Bloom, trạng thái và quyền course. Audit create/edit/approve/reject/attach. Test race duyệt/attach, cross-course, cùng revision vào nhiều phiên.

**Nghiệm thu:** lecturer tạo và duyệt câu không nhập rubric; đưa câu vào hai phiên Draft; publish pin version chung; F3 nhận cùng criteria/max cho mọi câu trong phiên; thay đổi bank/rubric active sau đó không làm đổi bài cũ.

## 5. F1-B — Import câu hỏi vào ngân hàng

- `POST /api/courses/{courseId}/question-bank/import` nhận JSON gồm content, topic, Bloom và tùy chọn câu mẫu/source reference; **không nhận rubric criteria**. Import vào session hiện có chỉ là API cũ, không đáp ứng bank import.
- Validate toàn batch trước transaction: dòng rỗng, Bloom sai, trùng trong batch/bank, course không có quyền; trả lỗi theo dòng/cột. Batch hợp lệ lưu toàn bộ dưới trạng thái Draft. Nếu cần CSV/XLSX, chốt template/giới hạn file riêng.
- Lecturer xem/sửa/duyệt từng Draft. Không auto-approve hoặc auto-attach sau import.

**Nghiệm thu:** import hợp lệ tạo Draft có Bloom/topic; import lỗi không lưu nửa chừng; lecturer course khác không thao tác được; không có ô nhập tiêu chí/điểm rubric.

## 6. F1-C — Sinh câu hỏi bằng AI từ nguồn

- Demo trước: `IQuestionGenerator` mock/deterministic nhận câu mẫu, topic/Bloom và **platform rubric version** để tạo Draft cho giảng viên duyệt. Đây là demo luồng nghiệp vụ, không được trình bày như RAG thật.
- Giai đoạn RAG thật: upload PDF/PPTX vào private storage; `course_materials` lưu checksum/version/trạng thái ingest, `material_chunks` lưu page/slide citation. Job ingest bền qua restart; retrieval filter course và material IDs. Không có bằng chứng phù hợp thì trả `insufficient_source`, không gọi LLM hoặc bịa citation.
- `POST /api/courses/{courseId}/question-bank/generations` nhận topic, Bloom, số câu và source IDs; backend tự đọc rubric active (hoặc version được pin cho job), truy hồi nguồn rồi gọi LLM. Output có cấu trúc gồm câu hỏi/Bloom/citation; **AI không sinh hay sửa tiêu chí rubric**. Validate nội dung, citation thuộc context, trùng câu, số lượng; lưu Draft cùng model/prompt/material/rubric version cho truy vết.
- Giảng viên sửa, approve/reject từng Draft trước khi attach. Chống prompt injection từ tài liệu; không log tài liệu private. Thay/xóa tài liệu không âm thầm sửa câu đã duyệt hoặc câu trong phiên.

**Nghiệm thu:** từ tài liệu đúng course có Draft kèm trang/slide trích dẫn; không lẫn course; không có nguồn thì không sinh; chỉ câu được lecturer duyệt mới attach được.

## 7. Diagram, kiểm thử và thứ tự PR

[Diagram F1](SequenceDiagram-F1-Question-Bank-RAG.drawio) mô tả `alt` nguồn câu mẫu/tài liệu, ingest bất đồng bộ, đọc platform rubric trước generation, `alt` thiếu/đủ bằng chứng, lecturer reject/approve và `opt` attach câu Approved. Diagram chỉ chứa workflow nghiệp vụ; CRUD list/detail nằm trong API contract.

Mốc tương đối dưới đây lấy **T0 = ngày bắt đầu sprint do team chốt**; PO/tech lead
cần gán người và ngày lịch cụ thể trước khi nhận PR.

| PR đề xuất | Phụ trách theo vai trò | Ước lượng sơ bộ | Deadline |
| --- | --- | --- | --- |
| F1-A1 BE: platform rubric version, schema/legacy migration, contract F3/F7, seed, khóa ghi rubric lecturer, PostgreSQL regression | BE AccessConfig + Interview | **8–12 ngày làm việc**; chốt lại sau spike dữ liệu lịch sử | Đề xuất T0 + 12 ngày làm việc |
| F1-A1 FE: bỏ bước nhập/sửa rubric, thêm màn xem rubric chỉ đọc | FE exam setup | 2–3 ngày làm việc | Sau khi contract F1-A1 ổn định + 3 ngày |
| F1-A2 BE: bank Draft/revision, review, attach câu vào session | BE AccessConfig | 3–5 ngày làm việc | Sau F1-A1 BE + 5 ngày |
| F1-A2 FE: bank list/edit, xem nguồn, duyệt/loại AI Draft, chọn câu Approved cho phiên | FE question bank | 3–5 ngày làm việc | Sau API F1-A2 + 5 ngày |
| F1-B BE: import JSON với lỗi theo dòng và transaction | BE AccessConfig | 1–2 ngày làm việc | Sau F1-A2 BE + 2 ngày |
| F1-B FE: form import, hiển thị lỗi theo dòng và trạng thái Draft | FE question bank | 1–2 ngày làm việc | Sau API F1-B + 2 ngày |
| F1-C demo BE: mock generation từ câu mẫu + rubric chung | BE AI/AccessConfig | 1–2 ngày làm việc | Sau F1-B BE + 2 ngày |
| F1-C demo FE: yêu cầu sinh câu, xem/chỉnh/duyệt Draft được tạo | FE question bank | 1–2 ngày làm việc | Sau API demo + 2 ngày |
| F1-C production BE/FE: material storage/ingest/RAG/provider thật và UI tài liệu/citation | BE AI + hạ tầng + FE | Ước lượng sau spike provider/tài liệu và contract UI | Cần chốt sau spike |

Ước lượng là đề xuất để lập sprint, chưa phải cam kết của người được phân công.
**Demo tối thiểu (BE + FE):** rubric chỉ đọc, bank/import, mock generation và
màn giảng viên sửa/duyệt trước attach. **Hoàn chỉnh:** RAG thật, citation kiểm
chứng, bảo mật tài liệu và test PostgreSQL. CI dùng fake storage/LLM; smoke
test provider/tài liệu riêng. Không chạy lại `database/01_schema.sql` trên DB
đang có dữ liệu.

## 8. Quyết định còn mở

- Ai seed/quản lý platform rubric sau demo; quy trình version/activate/rollback và cutover route rubric cũ.
- JSON hay thêm CSV/XLSX, kích thước batch/file, chính sách câu trùng và sửa snapshot trong phiên Draft.
- LLM, retrieval/embedding, storage, quota/ngân sách, retention và bản quyền giáo trình/slide. Chốt sau spike với tài liệu mẫu.
