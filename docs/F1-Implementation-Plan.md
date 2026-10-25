# Plan triển khai BE Feature 1 — Ngân hàng câu hỏi và rubric

Cập nhật: 2026-10-25; đối chiếu repo tại commit `05d7ef3`. Đây là plan, các mục chưa có code được ghi rõ là dự kiến.
Sequence diagram đề xuất: [F1 – Question Bank and RAG](SequenceDiagram-F1-Question-Bank-RAG.drawio).

## 1. Yêu cầu và kết quả cần đạt

Giảng viên tạo, nhập hàng loạt hoặc dùng AI sinh câu hỏi vấn đáp theo môn học/chủ đề từ giáo trình và slide bài giảng (RAG trên tài liệu môn học). Mỗi câu có nhãn mức độ nhận thức Bloom: `Remember` (nhớ), `Understand` (hiểu), `Apply` (vận dụng), `Analyze` (phân tích), và một rubric gồm tiêu chí + điểm tối đa. Câu AI sinh là **bản nháp**: giảng viên xem nguồn, sửa, duyệt hoặc loại bỏ trước khi đưa vào ngân hàng chính thức. Câu chính thức mới được chọn cho phiên thi; AI F3 chấm dựa trên rubric đã gắn vào phiên.

Phạm vi F1 là ngân hàng, tài liệu học phần, RAG, luồng duyệt và đưa câu hỏi vào phiên thi. F3 xử lý vấn đáp/chấm gợi ý; F6 đọc kết quả. Quyền tạo/quản lý thuộc lecturer được phân công course hoặc administrator theo chính sách hiện có.

## 2. Repo hiện có và khoảng trống

| Thành phần | Hiện trạng | Khoảng trống so với F1 |
| --- | --- | --- |
| `questions`, `rubrics`, `rubric_criteria` trong `database/01_schema.sql` | `questions.exam_session_id` bắt buộc; rubric gắn một câu hỏi trong phiên; max score được trigger tính từ criteria | Chưa có câu hỏi ở cấp course để tái sử dụng, chủ đề, Bloom, trạng thái duyệt, nguồn tài liệu hoặc lịch sử revision |
| `QuestionsController`/`QuestionService` | CRUD và `POST /api/exam-sessions/{sessionId}/questions/import` nhận danh sách JSON `{orderNo, content}`; chỉ sửa phiên Draft | Import này chỉ nhập vào **một phiên**, không phải ngân hàng; không nhập rubric/Bloom/chủ đề; không có file import |
| `RubricsController`/`RubricService` | CRUD rubric cho câu trong phiên Draft; mỗi criterion có `description`, `maxPoints`, `orderNo` | Chưa có rubric ở ngân hàng để dùng lại; không có quy trình duyệt câu AI |
| `ExamSessionCommands.PublishAsync` | Yêu cầu ít nhất một câu, mọi câu có rubric hợp lệ và có participant | Chưa có thao tác chọn câu chính thức từ ngân hàng |
| `IExamConfigurationApi` | F3 nhận câu hỏi + criterion theo thứ tự của phiên, có ID riêng | Cần giữ contract này ổn định; F3 không đọc bank draft/RAG trực tiếp |
| AI/config/storage | Có AI settings ở AccessConfig; F3 chỉ đăng ký mock; Docker chưa có object storage/vector service | Chưa có ingest tài liệu, retrieval, generation adapter, storage private, bằng chứng trích nguồn |

Nguồn kiểm tra: các file tương ứng ở `src/Modules/AccessConfig`, `src/Modules/Interview`, `database/01_schema.sql`, `docker-compose.yml`. CRUD question/rubric theo phiên hiện có chưa đáp ứng ngân hàng câu hỏi F1.

### Cách đọc sequence diagram F1

| Nhánh trong diagram | Mốc trong plan | Điều kiện chuyển bước |
| --- | --- | --- |
| A: tạo câu thủ công → Draft rubric/Bloom → duyệt → chọn cho phiên | F1-A | Chỉ revision Approved cùng course được copy vào phiên Draft; bản copy có ID riêng để F3 dùng |
| B: import JSON → validate cả batch → lưu Draft | F1-B | Dòng sai trả lỗi trước transaction; không tạo dữ liệu nửa chừng; từng câu vẫn cần lecturer duyệt |
| C: upload → ingest chunk/citation → retrieve → LLM → AI Draft → lecturer sửa/duyệt/loại | F1-C, sau đó F1-A để attach | Chỉ truy hồi tài liệu đúng course; thiếu nguồn thì dừng trước LLM; AI không tự đưa câu vào ngân hàng chính thức |

Diagram mô tả luồng **dự kiến**, không phải các API/worker đã tồn tại. Lifeline
`BankService`, `Ingest / Retrieval` và `ObjectStorage` cần được triển khai;
`PostgreSQL` là nơi lưu trạng thái bền vững. API cho F1 nằm trong AccessConfig,
còn F3 tiếp tục đọc câu/rubric snapshot qua `IExamConfigurationApi`.

## 3. Mô hình và quy tắc dữ liệu đề xuất

- Thêm `question_bank_items` thuộc `course_id` với `topic`, `bloom_level`, `content`, trạng thái `Draft/Approved/Rejected/Archived`, `origin` (`Manual/Import/Ai`), người tạo/duyệt và timestamps. Mỗi item có rubric/criteria tại bank, tổng điểm dương và không quá `999.99` như rubric hiện hành. Chỉ item `Approved` có rubric hợp lệ mới được thêm vào phiên.
- Lưu revision hoặc bản nội dung/rubric đã duyệt bất biến (`question_bank_revisions` + criteria tương ứng); mọi lần sửa câu đã duyệt tạo bản nháp revision mới, không thay đổi revision đã được dùng. Ghi nguồn AI, model/prompt version và chứng cứ trích dẫn trong metadata nội bộ.
- Tài liệu course (`course_materials`) lưu tên, loại, object key private, checksum, upload/ingest status và phiên bản; chunk (`material_chunks`) lưu vị trí trang/slide, đoạn văn và chỉ mục truy hồi. Chỉ tài liệu thuộc đúng course, ingest thành công và được phép dùng mới vào retrieval. Chọn định dạng PDF/PPTX trước, ghi rõ giới hạn file/scan/slide và xử lý extract lỗi.
- Đưa bank revision đã duyệt vào phiên **Draft** bằng cách copy nội dung và rubric/criteria sang `questions`/`rubrics`/`rubric_criteria` hiện có, tạo ID mới; `questions` thêm `source_bank_revision_id` nullable để truy vết. Có thể copy topic/Bloom nếu API phiên cần hiển thị. Sau publish, sửa/loại ngân hàng không thay đổi câu, criterion ID hoặc điểm của phiên và attempt cũ. Không dùng FK cascade từ bank sang câu phiên.
- Dữ liệu câu hỏi/rubric đang có trong các phiên được giữ nguyên. Không tự chuyển câu ở phiên Published/Closed thành bank item; nếu muốn tái sử dụng, làm thao tác “lưu vào ngân hàng” rõ ràng với kiểm tra quyền và chống trùng.
- Mỗi thay đổi schema cập nhật `database/01_schema.sql`, entity/EF mapping và một script nâng cấp tiến lên cho DB đang có dữ liệu. Không chạy lại `01_schema.sql` hoặc reset volume trên DB hiện hữu.

## 4. Mốc F1-A — Ngân hàng thủ công và rubric

API đề xuất dưới `/api/courses/{courseId}/question-bank`:

| Method | Route | Hành vi |
| --- | --- | --- |
| GET | `/` và `/{itemId}` | Lọc/search theo topic, Bloom, trạng thái, origin; trả rubric và revision |
| POST | `/` | Tạo draft thủ công cùng rubric hoặc lưu draft rồi cập nhật rubric |
| PUT | `/{itemId}` | Sửa draft/revision draft, gồm nội dung, topic, Bloom, criteria |
| POST | `/{itemId}/approve` | Duyệt revision hợp lệ; ghi người và giờ duyệt |
| POST | `/{itemId}/reject` | Loại draft AI/nhập với lý do; không xóa cứng provenance |
| POST | `/api/exam-sessions/{sessionId}/questions/from-bank` | Chọn revision Approved của cùng course, copy atomically vào phiên Draft, xác định `orderNo` |

- Dùng role Lecturer/Admin **và** kiểm tra lecturer được phân công course ở service, giống QuestionService hiện có. Không cho cross-course attach, sửa published session, hoặc chọn draft/rejected. Audit create/update/approve/reject/attach.
- Validate Bloom chỉ thuộc bốn mức trên; topic/content không rỗng; criterion có thứ tự duy nhất, `maxPoints > 0`, tổng điểm dương `<= 999.99`; không trả lỗi DB thô. Attach idempotent theo revision + session hoặc trả `409` rõ ràng khi trùng order/item theo policy được chốt.
- Giữ route CRUD câu hỏi/rubric theo phiên để tương thích FE hiện tại; plan F1-A cần quyết định có cho chỉnh bản copy trong phiên Draft hay buộc chọn lại revision. Nếu cho chỉnh, bản copy trở thành snapshot riêng, vẫn không sửa bank.

**Nghiệm thu:** giảng viên tạo câu theo course/chủ đề/Bloom, rubric hợp lệ, duyệt và thêm vào hai phiên Draft; sửa ngân hàng hoặc phiên thứ nhất không đổi phiên thứ hai; F3 đọc đúng nội dung/criterion qua contract cũ.

## 5. Mốc F1-B — Import ngân hàng

- Thiết kế `POST /api/courses/{courseId}/question-bank/import` cho danh sách JSON có question, topic, Bloom và rubric criteria; nếu cần CSV/XLSX, chốt template/version, giới hạn kích thước và format ở bước này. Import hiện tại theo phiên tiếp tục hoạt động nhưng không được coi là F1 import đầy đủ.
- Validate toàn bộ batch trước khi ghi; trả lỗi theo dòng/cột, phát hiện câu rỗng, Bloom sai, rubric thiếu/điểm sai, trùng trong batch và policy trùng với bank. Chọn transaction all-or-nothing cho batch nhỏ; nếu file lớn cần job có trạng thái và báo cáo riêng.
- Tất cả item nhập là Draft; giảng viên xem/chỉnh/duyệt trước khi attach. Không auto-publish vào bank chính thức chỉ vì import thành công.

**Nghiệm thu:** import hợp lệ tạo draft đầy đủ rubric/Bloom; dòng lỗi chỉ rõ vị trí và không sinh dữ liệu nửa chừng; lecturer course khác không xem/sửa được.

## 6. Mốc F1-C — Tài liệu và RAG sinh câu hỏi

- API đề xuất: upload/list/delete tài liệu ở `/api/courses/{courseId}/materials`; `POST /api/courses/{courseId}/question-bank/generations` nhận topic, Bloom mục tiêu, số câu, tài liệu được phép; GET job/draft để FE hiển thị nguồn và tiến độ. Upload kiểm tra quyền, MIME/file signature, kích thước, checksum; object storage private, chỉ cấp quyền đọc có kiểm soát.
- Ingest tách PDF/slide thành đoạn có page/slide citation, version theo checksum. Job bền vững qua restart, retry giới hạn; thay/xóa tài liệu vô hiệu chỉ mục tương ứng, giữ provenance cần thiết của câu đã duyệt theo policy lưu trữ được chốt. Retrieval luôn filter `course_id` và material IDs; test không lẫn dữ liệu course khác.
- Chọn retrieval adapter và LLM sau thử nghiệm mẫu (chất lượng tiếng Việt, PDF/PPTX, độ trễ/quota/chi phí). Có thể bắt đầu bằng PostgreSQL full-text cho chunk; nếu cần semantic retrieval, thêm embedding/index sau khi xác nhận hạ tầng và migration. Prompt đưa đoạn trích và yêu cầu output có cấu trúc; validate câu hỏi, Bloom, rubric, tổng điểm, citation ID trỏ đúng chunk đã truy hồi. Không có nguồn phù hợp thì báo không đủ căn cứ, không tự bịa citation.
- AI chỉ tạo Draft; giảng viên duyệt/chỉnh/loại từng câu. Lưu model/prompt version, material version, chunk IDs và trích dẫn để truy vết; không đưa nội dung tài liệu private vào log. Hạn chế prompt injection từ tài liệu và tránh sinh câu hỏi chứa nguyên đáp án/PII.

**Nghiệm thu:** từ tài liệu đúng course sinh draft có citation trang/slide, Bloom và rubric hợp lệ; lecturer sửa/duyệt rồi attach; câu bị reject không dùng được; thay tài liệu không âm thầm sửa câu phiên đã phát hành.

## 7. Kiểm thử, tích hợp và thứ tự

- Unit: validation Bloom/rubric, quyền course, trạng thái duyệt, snapshot attach, import lỗi từng dòng, parse output AI. HTTP contract: role, route, payload/error, publish session sau attach.
- PostgreSQL integration riêng: FK/unique/transaction, attach đồng thời, revision, publish với rubric, migration từ dữ liệu cũ. Dùng fake retrieval/LLM/storage trong CI; smoke test provider và tài liệu mẫu riêng, đo citation đúng/không lẫn course.
- Cập nhật `AccessConfig` domain/service/controller/Contracts khi cần, SQL forward upgrade, OpenAPI/README và mẫu import cho FE. Tránh để module Interview tham chiếu implementation ngân hàng.
- Thứ tự: **F1-A → F1-B → F1-C**. F1-A có thể làm song song F3 lifecycle; F3 provider thật chỉ cần rubric snapshot qua contract hiện tại. F1 Done khi giảng viên tạo/import/sinh bằng RAG, duyệt trước khi đưa vào ngân hàng, chọn cho phiên Draft và thi/chấm qua F3 mà dữ liệu lịch sử không đổi.

## 8. Điểm cần chốt với nhóm trước khi code tương ứng

- Import chỉ JSON hay cần CSV/XLSX; giới hạn batch/file và template FE.
- Chính sách sửa bản copy trong phiên Draft; cách xử lý item/revision đã dùng khi lecturer archive hoặc yêu cầu xóa tài liệu.
- Storage, LLM và retrieval/embedding provider, quota/ngân sách, retention tài liệu/chunk và quy định bản quyền đối với giáo trình/slide.
