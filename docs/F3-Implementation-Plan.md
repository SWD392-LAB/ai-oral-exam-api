# Plan triển khai BE Feature 3 — AI Viva Interview

Cập nhật: 2026-10-25. Đối chiếu repo tại commit `793cd73` và feedback PR #7.

Đây là kế hoạch cho phần F3 **còn thiếu**. Các mục đã có trong repo được đánh dấu
rõ ở dưới; chưa khẳng định E2E với provider thật. F1 có [plan riêng](F1-Implementation-Plan.md).
Sequence diagrams: [thi và duyệt điểm](SequenceDiagram-F3-AI-Viva-Interview.drawio),
[giảng viên xem kết quả/thống kê/xuất bảng điểm](SequenceDiagram-F3-Lecturer-Results.drawio).
Các PR dưới đây bám theo thứ tự thông điệp trong hai diagram.

## 1. Mục tiêu và phạm vi

F3 hoàn thành khi sinh viên thi vấn đáp bằng microphone, AI đọc câu hỏi và hỏi phụ,
BE lưu audio/hội thoại, đề xuất điểm theo **rubric chung của platform đã pin cho
phiên thi**, giảng viên xác nhận điểm cuối,
và F6 đọc được kết quả của bài thi mới.

Các hành vi bắt buộc:

- Chỉ sinh viên trong danh sách dự thi được bắt đầu trong thời gian phiên mở.
  Resume trả attempt `InProgress`; thi lại tuân theo `AllowRetake` (mặc định
  `false`). Schema hiện cho phép nhiều attempt trên một participant.
- Mỗi câu trả lời có deadline riêng, bao gồm câu hỏi phụ; BE quyết định thời gian.
- Hỏi phụ theo nội dung câu trả lời và không vượt `MaxFollowUps` của câu chính.
- Chấm sau khi câu chính và các lượt hỏi phụ kết thúc; điểm câu bằng tổng điểm
  từng criterion. Giảng viên quyết định một điểm cuối cho toàn bộ attempt.
- Đến `ExamSession.EndTime`, ngừng hỏi và nhận câu trả lời mới, kể cả khi FE mất
  kết nối. Câu không trả lời tính 0. Câu chưa được hỏi không cần tạo response giả.
- Lưu nguyên văn câu hỏi thực sự đã hỏi, transcript, timestamp và audio trả lời.
- AI lỗi phải giữ câu trả lời đã nhận; lỗi dịch vụ không phải lý do chấm 0.
- Sinh viên không nhận điểm AI trước khi giảng viên xác nhận.

BE dùng modular monolith hiện tại. F3 ghi các bảng Interview; AccessConfig sở
hữu cấu hình phiên thi/câu hỏi và rubric nền tảng, AI settings; F6 chỉ đọc kết quả. Giao
tiếp giữa modules qua Contracts. F1 đưa câu đã duyệt vào phiên bằng snapshot;
F3 không đọc bản nháp AI hay tài liệu RAG trong lúc thi.

## 2. Những phần đã có và sẽ dùng tiếp

| Thành phần | Vai trò hiện có |
| --- | --- |
| `InterviewService.cs` | Start/resume, nhận text, phân tích, chấm, chuyển câu, hoàn thành bài |
| `InterviewAttempt.cs` | `InProgress → PendingReview → Finalized` |
| `QuestionResponse.cs`, `InterviewTurn.cs` | Turns, điểm criterion, tổng AI, feedback, AudioUrl nullable; `ScoreReview` thuộc attempt |
| `InterviewDbContext.cs` | Mapping và lưu dữ liệu F3 |
| `Application/Ai/` | Các interface analyzer, scorer, LLM, STT, TTS trong file riêng; registration hiện dùng mock |
| `IExamConfigurationApi` | Câu hỏi/rubric, phiên thi, kiểm tra giảng viên phụ trách |
| `InterviewResultsApi` | Contract đọc kết quả cho F6 |
| `InterviewAttemptReviewsController.cs`, `InterviewReviewService.cs` | Đã có GET review, PUT score-review, lecturer authorization và chuyển Finalized |
| `database/01_schema.sql`, `05_allow_interview_attempt_retakes.sql` | Schema cho DB mới và nâng cấp thi lại; không còn unique `participant_id` |
| `ExamSessionAutoCloseService.cs` (AccessConfig) | Đã tự đóng trạng thái phiên Published khi hết `EndTime`; chưa đóng attempt đang thi |
| `tests/AiOralExam.IntegrationTests` | Đã có HTTP contract tests với EF InMemory/fake auth; chưa có PostgreSQL integration cho race/constraint |

Khoảng trống hiện tại: toàn bộ AI dùng mock; chưa có audio API/storage, SignalR,
worker kết thúc **attempt**; submit chưa nhận `turnId` và chưa idempotent. Kiểm tra
hết giờ câu chỉ chạy khi submit, chưa kiểm tra `EndTime` trong submit. Đặc biệt
`ShowAiScoreToStudent` ở default options và `appsettings.json` vẫn là `true`, có
thể lộ điểm AI trong submit trước khi lecturer duyệt.

**Độ lệch schema với yêu cầu mới:** hiện `rubrics.question_id` và
`rubric_criteria` là dữ liệu theo câu, lecturer có thể sửa trong phiên Draft.
F1-A1 sẽ đưa vào platform rubric version do administrator quản lý, pin version
khi publish và chuyển F3 sang criterion IDs của version đó. Session/attempt cũ
vẫn đọc rubric cũ; xem mục 3 trong [plan F1](F1-Implementation-Plan.md).

### Đối chiếu sequence diagram → PR

| Bước trong diagram F3 | PR chịu trách nhiệm | Điều chỉnh khi triển khai |
| --- | --- | --- |
| Student bắt đầu; kiểm tra danh sách, giờ phiên; tạo attempt hoặc trả lý do | PR 02 | Diagram đã thể hiện resume/retake; chống start đồng thời trên PostgreSQL |
| Tải câu tiếp theo, tạo `QuestionResponse`/turn | PR 02 | Persist câu hỏi trước khi trả UI/push event; không tạo câu mới sau `EndTime` |
| TTS câu chính/câu hỏi phụ, phát âm thanh | PR 04, PR 05 | PR 04 phát theo lượt; PR 05 đẩy sự kiện realtime |
| Ghi âm; dừng bởi submit/deadline/EndTime; gửi audio | PR 02, PR 04, PR 05 | PR 02 chốt deadline/idempotency; PR 04 upload/storage; PR 05 stream/chunk/reconnect |
| STT → transcript → lưu turn | PR 04, PR 05 | Lưu audio và marker nhận trước STT; partial transcript chỉ ở PR 05 |
| Analyzer kiểm tra câu trả lời, quota → LLM hỏi phụ hoặc scorer chấm rubric | F1-A1, PR 03 | Criteria của session mới lấy từ platform rubric version đã pin; không gọi AI trên partial, lỗi có trạng thái retry |
| Hết bài/hết phiên → `PendingReview`; câu chưa tới được tính 0 | PR 02, PR 03 | Không tạo response giả cho câu chưa hỏi; tổng chỉ cộng response đã có |
| Lecturer mở review, chỉnh điểm/ghi chú rồi xác nhận | PR 01 | GET chỉ đọc; tạo `ScoreReview Pending` ở lần lưu Draft đầu tiên, giữ PUT một bước hiện có |
| `Approved` + `Finalized`, báo sinh viên điểm đã xác nhận | PR 01, PR 05, PR 06 | Persist trước sự kiện; F6 chỉ mô tả luồng feedback của sinh viên; PR 05 thêm thông báo realtime |

Diagram mô tả happy path. Resume/retake, lỗi AI/storage, bảo mật, idempotency,
worker offline và PostgreSQL concurrency là phần mở rộng bắt buộc của plan;
diagram không chứng minh các luồng đó đã có code.

## 3. Các quyết định cần chốt và quy tắc triển khai

### Provider và hạ tầng

- Chọn một provider LLM, một STT và một TTS; có thể cùng nhà cung cấp.
- Repo chưa có adapter provider thật/credential triển khai được xác minh. Việc
  chốt provider là điều kiện bắt đầu PR 03–05, không chặn hardening review/PR 02.
- Chọn Object Storage tương thích S3. Chốt service/endpoint và image Docker
  cụ thể trước PR 04; bucket audio là private.
- Trước khi chốt STT, kiểm tra khả năng streaming/partial transcript,
  tiếng Việt/Anh, codec, sample rate, quota và latency bằng một audio mẫu.
- Dùng HTTP/SDK phù hợp provider sau bước kiểm tra tài liệu chính thức;
  business service tiếp tục gọi các interface hiện có.
- API key/credential lấy từ environment hoặc user-secrets; commit file mẫu
  cấu hình không chứa credential thật.

**Bước readiness — chọn và thử provider:**

- [ ] Đối chiếu tài liệu chính thức, khả năng hỗ trợ tiếng Việt/Anh, streaming
  STT, output có cấu trúc cho LLM, giá/quota và codec với nhu cầu dự án.
- [ ] Thử một câu hỏi/rubric với LLM; thử một audio mẫu Việt/Anh cho STT và TTS.
- [ ] Ghi kết quả về chất lượng, latency quan sát được, chi phí và giới hạn.
- [ ] Chốt một lựa chọn cho từng dịch vụ, cấu hình credential và bổ sung tài
  liệu API chính thức vào plan trước khi bắt đầu adapter tương ứng.

Thực hiện readiness song song với hardening review và PR 02. Các việc này dùng
mock hiện có, không phụ thuộc tài khoản/quota của provider thật.

### Trạng thái và dữ liệu

- Attempt giữ nguyên ba trạng thái hiện tại. Trạng thái xử lý AI nằm ở response,
  không thêm trạng thái của provider vào `AttemptStatus`.
- `Approved` và `Finalized` hiện được lưu trong một `SaveChangesAsync`; cần
  kiểm chứng transaction và chống duyệt đồng thời trên PostgreSQL thật.
- Submit nhận `turnId`; `CurrentTurnDto` trả thêm `turnId`.
- Chốt nhận câu trả lời bằng thời gian server nhận dữ liệu, không bằng thời
  gian LLM/STT xử lý xong và không tin `durationSeconds` từ FE.
- Giữ quy ước `AnsweredAt = null` khi không có câu trả lời. Thêm marker xử lý
  riêng để phân biệt một turn chưa submit với một turn đã submit rỗng/hết giờ.
- Unique/index và concurrency guard trong SQL là hàng rào cuối cùng; chuyển lỗi thành
  kết quả idempotent hoặc `409`, thay vì `500`.
- Không giữ transaction/row lock trong khi chờ provider hoặc Object Storage.

### Thay đổi schema dự kiến

PR 02 thêm marker turn đã được nhận/xử lý và hash nội dung để kiểm tra retry cùng
payload, kể cả câu trả lời rỗng. PR 03 thêm trạng thái xử lý AI của response,
timestamp phục hồi xử lý bị gián đoạn và mã lỗi nếu cần cho retry.

Chốt tên cột/CHECK constraint khi triển khai từng PR; cập nhật entity và mapping
cùng SQL. Khi cần retry, phải lưu đủ trạng thái trong DB để restart không mất
câu trả lời. Các job hết hạn/AI đọc trạng thái DB, không phụ thuộc timer trong RAM.

Update `01_schema.sql` cho cài mới và cung cấp script nâng cấp có version cho DB
đang có dữ liệu. Không reset volume ứng dụng hoặc Keycloak để triển khai thay đổi.

## 4. PR 01 — Hardening review và quyền riêng tư (API đã có)

`GET /api/interview-attempts/{id}/review` và
`PUT /api/interview-attempts/{id}/score-review` đã có trong
`InterviewAttemptReviewsController`. Service đã kiểm tra lecturer được phân công,
chỉ duyệt `PendingReview`, validate điểm `0..TotalMaxScore` (tối đa hai chữ số
thập phân), lấy reviewer/time từ principal/server và lưu Approved + Finalized.

Phần còn phải làm:

- Cho luồng `getAttemptReview → adjustScore → confirmScore` của diagram:
  GET chỉ đọc; mở rộng review DTO để trả `suggestedTotalScore` cho UI khởi tạo
  điểm mặc định (không tạo review khi mở trang). Bổ sung
  `PUT /api/interview-attempts/{id}/score-review/draft` lưu/cập nhật
  `ScoreReview Pending` và
  `POST /api/interview-attempts/{id}/score-review/confirm` xác nhận draft.
  Giữ `PUT /score-review` hiện có để client cũ chốt trong một bước; cả hai
  đường xác nhận dùng cùng validation/concurrency guard.
- Đặt `ShowAiScoreToStudent=false` ở `InterviewOptions` và `appsettings.json`;
  kiểm tra response Student ở submit, F6 và event tương lai không lộ điểm AI
  trước khi duyệt.
- Kiểm tra các response đã nhận đều xử lý điểm xong; khi AI còn pending/lỗi,
  từ chối duyệt với `409 evaluation_pending` sau PR 03.
- Hiện retry sau `Finalized` luôn trả conflict. Cho retry cùng lecturer/cùng
  payload trả review đã lưu; payload khác trả `409`. Không mở lại điểm.
- Chống hai lecturer duyệt đồng thời bằng concurrency guard/transaction ngắn;
  test trên PostgreSQL thật. Draft chỉ người được phân công tạo/sửa; confirm
  lấy reviewer/time từ principal/server. Giữ controller/service hiện có.

**Nghiệm thu:** bài text/mock → lecturer duyệt → F6 có điểm cuối, statistics/CSV
đúng; test draft → chỉnh → confirm, PUT một bước cũ, sai quyền, bài đang thi,
retry, race và không lộ điểm trước duyệt.

## 5. PR 02 — Deadline, submit idempotent và tự kết thúc phiên

Branch đề xuất: `feat/f3-session-lifecycle`.

- Thêm `turnId` bắt buộc vào submit và thêm ID vào DTO của câu hỏi hiện tại.
- Nhánh bắt đầu trong diagram phải có đủ: không đủ điều kiện → lý do rõ ràng;
  attempt đang chạy → resume; hết lượt và `AllowRetake=false` → conflict;
  được thi lại → tạo attempt mới. Lưu câu chính/turn trước khi trả UI.
- FE submit lại cùng turn/payload không làm chấm hoặc chuyển câu lần thứ hai;
  payload khác cho turn đã nhận trả `409`. Không tự áp câu trả lời cũ vào turn mới.
- Start đồng thời chỉ tạo một attempt `InProgress`, trả attempt đang chạy.
  Nếu đã có attempt kết thúc, chỉ tạo lượt mới khi `AllowRetake=true`; không
  khôi phục unique một attempt/participant đã bỏ trong schema.
- Deadline hữu hiệu là thời điểm sớm hơn giữa deadline câu (gồm grace hiện tại)
  và `ExamSession.EndTime`. Grace không kéo dài phiên thi.
- Thống nhất phiên đóng khi `now >= EndTime` ở mọi chỗ kiểm tra.
- Thêm `InterviewExpirationWorker` dùng `BackgroundService` và `TimeProvider`.
  Worker quét deadline đã lưu; start/get/submit cũng kiểm tra để worker chạy trễ
  không mở thêm thời gian thi.
- Hết giờ câu: đóng turn, không hỏi phụ cho câu đã hết giờ; tính các câu trả lời
  đã nhận và chuyển câu khi phiên còn mở. Mỗi lượt hỏi phụ có deadline mới.
- Hết giờ phiên: đóng attempt thành PendingReview, không hỏi câu mới; câu chưa
  trả lời tính 0, giữ nguyên câu đã trả lời và các điểm đã có. Câu chưa được
  hỏi đóng góp 0 vào tổng nhưng không tạo `QuestionResponse` giả như chú thích
  ở bước `complete()` của diagram.
- Nếu câu trả lời đã nhận đang chờ AI, cho phép xử lý điểm tiếp nhưng không tạo
  câu hỏi mới. PR 01 phải từ chối duyệt với `409 evaluation_pending` cho tới khi
  xử lý xong; failure không được biến thành điểm 0.
- AccessConfig đã có worker đóng các session Published quá hạn **và**
  `ExamSessionCommands.CloseAsync` cho lecturer đóng thủ công; diagram F7 giữ
  cả hai nhánh. F3 phải ngừng hỏi/nhận khi status đã Closed hoặc `EndTime` tới.
  F3 không ghi trực tiếp `exam_sessions`; chỉ bổ sung contract đọc/batch nếu cần.
- Serialize các mutation cùng attempt bằng transaction ngắn/row lock ở DB;
  thử race submit–submit, submit–timeout và start–start trên PostgreSQL thật.

File dự kiến: `InterviewService.cs`, `SubmitAnswerRequest.cs`, `CurrentTurnDto.cs`,
domain/mapping Interview, worker mới, SQL nâng cấp và `InterviewModule.cs`;
AccessConfig Contracts chỉ đổi nếu thực sự cần.

**Nghiệm thu:** FE ngắt kết nối vẫn kết thúc bài; restart worker vẫn tìm được
deadline; retry không nhân đôi turn/criterion scores; mọi check dùng giờ server.

## 6. PR 03 — LLM thật cho hỏi phụ và rubric scoring

Branch đề xuất: `feat/f3-ai-adapters`.

- Cài provider adapter cho `ILlmService`; triển khai analyzer/scorer dùng adapter.
- PR này phụ thuộc F1-A1: session mới phải pin một platform rubric version.
  `IExamConfigurationApi` vẫn trả `QuestionInfo.Criteria` cho mỗi câu nhưng
  project cùng bộ criteria từ version đã pin; session cũ tiếp tục đọc rubric
  lịch sử theo câu. Lecturer không truyền/chỉnh rubric ở F3.
- Analyzer nhận câu chính, rubric, lịch sử lượt hỏi và hạn mức follow-up;
  yêu cầu output có cấu trúc theo `AnswerAnalysis`.
- Theo hai nhánh `alt` của diagram: chỉ tạo hỏi phụ khi câu trả lời chưa đủ,
  còn quota **và** phiên còn mở; câu đủ hoặc hết quota/hết phiên thì chấm câu
  đã nhận và không tạo hỏi phụ mới. TTS của hỏi phụ thuộc PR 04.
- Scorer nhận toàn bộ dialogue của một câu chính, trả điểm/nhận xét từng criterion.
  Với session mới, criterion IDs thuộc platform rubric version đã pin. BE tính
  điểm tối đa một câu bằng tổng `max_points` của version đó, cùng giá trị cho
  mọi câu; tổng điểm tối đa phiên bằng số câu nhân điểm tối đa một câu. Provider
  không quyết định điểm cuối của attempt.
- Validate criterion ID, thiếu/thừa/trùng criterion, range điểm, JSON sai và
  follow-up rỗng. Kết quả không hợp lệ là lỗi cần retry, không âm thầm chấm 0.
- Lưu câu trả lời trước khi gọi AI. Persist kết quả xử lý một lần; re-check trạng
  thái phiên/attempt trước khi tạo follow-up hoặc chuyển câu.
- Timeout và retry có giới hạn; phân biệt lỗi credential, quota, network và
  output sai. API/state thể hiện câu trả lời đã lưu nhưng đang chờ xử lý.
- Phục hồi response xử lý dở khi process restart; claim công việc trong DB để
  tránh ghi kết quả/chấm hai lần. Có thao tác retry rõ ràng cho lỗi không tự phục hồi.
- Câu trả lời sinh viên được đưa vào prompt như dữ liệu cần đánh giá; test câu
  trả lời yêu cầu AI bỏ rubric/cho điểm tối đa.
- Ngôn ngữ/provider settings đọc từ F7 contract; phần bổ sung tối thiểu là
  operation đọc cấu hình, không cần hoàn thành toàn bộ màn hình quản trị F7.
- Giữ mock để chạy test deterministic; cấu hình triển khai chỉ dùng adapter thật
  sau khi health/config check thành công.

File dự kiến: `Application/Ai/` adapters/analyzer/scorer và interfaces tương ứng,
`InterviewService.cs`, `InterviewModule.cs`, options/config, SQL và F7 contract.

**Nghiệm thu:** một bài text hoàn chỉnh với LLM thật, có câu hỏi phụ đúng quota,
điểm từng criterion được lưu; dịch vụ lỗi không mất transcript hoặc lộ điểm cho SV.

## 7. PR 04 — Audio, Object Storage, STT và TTS

Branch đề xuất: `feat/f3-voice-records`.

API dự kiến:

| Method | Route | Hành vi |
| --- | --- | --- |
| POST | `/api/interview-attempts/{id}/audio` | Multipart `turnId` + `file`; lưu audio và STT rồi đi qua cùng submit pipeline |
| GET | `/api/interview-attempts/{id}/turns/{turnId}/question-audio` | TTS của câu hỏi, có kiểm tra quyền và trạng thái turn |
| GET | `/api/interview-attempts/{id}/turns/{turnId}/answer-audio` | Phát audio lưu trữ theo quyền review/report |

- Triển khai `ISpeechToTextService` và `ITextToSpeechService` bằng provider đã chọn.
- STT dùng tiếng Việt/Anh từ cấu hình. Timestamp nhận audio được chốt trước khi
  STT chạy; latency STT không làm một câu trả lời hợp lệ thành quá hạn.
- Theo diagram, TTS phải sẵn cho cả câu chính và hỏi phụ trước khi UI phát;
  audio nhận phải được lưu/tham chiếu trước khi transcript cuối được ghi vào
  turn. PR 04 trả question text + audio theo lượt; PR 05 mới push realtime.
- Quy định codec, content type, kích thước/duration tối đa với FE; kiểm tra file
  ở server, object key do server tạo và gắn vào đúng turn/attempt.
- Lưu một bản audio trả lời cho mỗi turn; DB lưu object key/reference, không
  dùng URL public hoặc URL ký có hạn làm địa chỉ lưu trữ lâu dài.
- Khi lỗi ghi DB/storage, không công bố record thiếu audio; có cơ chế dọn file
  mồ côi theo trạng thái/reference. Retry không tạo nhiều audio cho cùng submission.
- Lecturer phụ trách/admin đọc audio để review; student đọc bản lưu theo chính
  sách report F6 sau khi xác nhận. TTS câu hiện tại phục vụ sinh viên đang thi.
- Review DTO cung cấp route phát audio có authorization, không lộ credential/bucket.
- Thêm service storage vào Docker/config local sau khi đã chốt implementation.

File dự kiến: audio controller/service, provider adapters, storage adapter,
Interview domain/mapping, review DTO, Docker/config và tests.

**Nghiệm thu:** thi một lượt bằng audio, nghe được TTS, transcript đi vào cùng
luồng AI, lecturer phát lại đúng audio; audio vẫn truy cập đúng sau restart.
Đây là mốc voice theo lượt, chưa phải nghiệm thu near-realtime STT.

## 8. PR 05 — SignalR, partial transcript và reconnect

Branch đề xuất: `feat/f3-realtime-interview`.

- Đăng ký SignalR và map `/hubs/interview` trong host.
- Authentication dùng Keycloak; kiểm tra ownership trước khi join group attempt
  và trước mỗi thao tác. Client không tự chọn group để đọc bài người khác.
- Chốt contract: `JoinAttempt`, `SendAudioChunk`, `CompleteAnswer`;
  events `TranscriptUpdated`, `QuestionReady`, `AttemptUpdated`, `InterviewError`.
- Payload chung có attemptId, turnId và version trạng thái; gửi schema/payload
  mẫu cho FE trước khi FE đổi cách submit. Chuẩn hóa mã lỗi trong `ApiError`.
- Mỗi audio chunk có turnId và sequence; chunk lặp không cộng transcript/audio
  hai lần, sai thứ tự được xử lý theo contract đã chốt.
- Persist checkpoint/sequence đã nhận và phần audio tạm trước khi ACK. Khi
  reconnect/restart, tiếp tục từ checkpoint hoặc dựng lại STT stream từ audio đã
  lưu; không coi việc nối lại SignalR là đã tự khôi phục được stream của provider.
- Mở rộng STT contract để trả partial/final transcript bằng streaming provider.
  Không giả định từng chunk WebM là một file độc lập có thể STT riêng.
- Phát hiện kết thúc trả lời bằng `CompleteAnswer` hoặc deadline server, không
  gọi analyzer/scorer trên mỗi partial transcript.
- Ghép/lưu audio hoàn chỉnh, chốt transcript cuối rồi chạy pipeline PR 03.
- Persist câu hỏi/trạng thái trước khi push event. GET attempt là nguồn sự thật;
  reconnect gọi GET/join để phục hồi, không phụ thuộc event cũ còn trong RAM.
- Thống nhất một submit pipeline cho text, multipart và streaming để tránh
  hai nguồn transcript cùng chấm một turn. UI chọn một chế độ trả lời cho turn.
- Chỉ gửi lỗi/trạng thái xử lý cho student; events không chứa điểm AI chưa duyệt.
- Sau PR 01 xác nhận điểm và commit `Approved`/`Finalized`, phát `AttemptUpdated`
  cho đúng sinh viên như bước “confirmed (student is notified)” trong diagram;
  client đọc F6 để lấy điểm đã được công bố. Reconnect GET phục hồi nếu mất event.

File dự kiến: hub mới, host `Program.cs`, streaming STT adapter/contract,
Interview service/DTO và integration tests.

**Nghiệm thu:** transcript xuất hiện trước khi trả lời xong; nhận câu hỏi tiếp
theo qua hub; reload/reconnect không mất bài hoặc submit trùng; hai sinh viên
đồng thời không nhận audio/transcript/events của nhau.

## 9. PR 06 — Nghiệm thu tích hợp và CI

Branch đề xuất: `test/f3-f6-integration`.

- Mở rộng HTTP contract tests đã có ở `tests/AiOralExam.IntegrationTests/`:
  `ApiTestFactory` hiện dùng EF InMemory, fake auth/Keycloak. Bổ sung suite
  PostgreSQL thật cho constraint, transaction, worker và race; AI fake
  deterministic cho CI, provider thật có smoke test riêng.
- Suite PostgreSQL chạy trên database/container riêng, seed tối thiểu và tự
  cleanup, không thay đổi dữ liệu phát triển hiện có. Giữ unit/HTTP tests hiện tại.
- Kiểm tra transaction với SQL constraint/trigger thật, không chỉ fake Contracts.
- Bộ test bắt buộc: eligibility, một attempt, resume, sai ownership, follow-up
  quota, điểm rubric, deadline câu/phiên, concurrency/retry, lỗi AI, storage,
  stream chunks, reconnect, duyệt điểm và F6 trước/sau duyệt.
- Dùng sequence diagram làm kịch bản nghiệm thu: nhánh từ chối bắt đầu;
  câu chính → TTS → audio/STT → hỏi phụ hoặc chấm; hoàn thành `PendingReview`;
  mở review → lưu/chỉnh draft → confirm → `Finalized` → thông báo student.
  Thử thêm đường PUT một bước hiện có và trường hợp event đến sau commit.
- Theo [diagram giảng viên](SequenceDiagram-F3-Lecturer-Results.drawio), kiểm
  tra **sau Finalized**: danh sách kết quả → confirmed-only statistics → CSV.
  Việc mở, lưu nháp và xác nhận review chỉ nằm trong diagram F3 chính.
  Controller/service thống kê và xuất file vẫn thuộc Reporting trong code;
  diagram này được đặt dưới F3 vì là luồng giảng viên sau khi chốt bài.
- Theo [diagram F6](SequenceDiagram-F6-Feedback-Reporting.drawio), chỉ nghiệm
  thu luồng sinh viên xem feedback: chưa duyệt chỉ có metadata, Approved mới
  có hội thoại/điểm. Không suy ra rằng các API lecturer Reporting bị xóa.
- Test session mới dùng cùng criterion IDs/max của platform rubric version,
  session cũ vẫn đọc đúng điểm theo rubric cũ; kích hoạt rubric version mới
  không đổi điểm/giới hạn của attempt lịch sử.
- E2E nghiệm thu: đăng nhập Keycloak → nhận câu hỏi/TTS → trả lời microphone →
  thấy partial transcript → hỏi phụ → hoàn thành → giảng viên nghe/đọc và duyệt →
  sinh viên xem F6 → lecturer tải CSV và xem statistics.
- Kiểm tra thêm phiên không có điểm đã duyệt, bài bỏ dở/hết giờ và câu chưa tới.
- Đưa build/test vào CI; cập nhật README, OpenAPI, payload/error/event mẫu cho FE.
- Giữ bằng chứng smoke test thật: cấu hình provider/model, thời gian chạy,
  latency quan sát được và kết quả; không đưa transcript/credential thật vào log CI.

## 10. Thứ tự thực hiện và Definition of Done

`Provider/storage readiness` chạy song song `PR 01 → PR 02` (PR 01 là hardening
API đã có). F1-A1 migration rubric phải xong trước PR 03 và trước khi publish
session theo quy tắc mới. Sau đó: `PR 03 → PR 04 → PR 05 → PR 06`.

| PR | Phụ trách theo vai trò | Ước lượng sơ bộ | Deadline |
| --- | --- | --- | --- |
| PR 01: review Draft/confirm, quyền riêng tư | BE Interview | 2–3 ngày làm việc | Đề xuất T0 + 3 ngày làm việc |
| PR 01 FE: màn lưu nháp/chỉnh/xác nhận điểm; ẩn điểm AI với student trước duyệt | FE review/interview | 1–2 ngày làm việc | Sau contract PR 01 + 2 ngày |
| PR 02: lifecycle/idempotency/worker | BE Interview + AccessConfig | 3–5 ngày làm việc | Đề xuất T0 + 8 ngày làm việc |
| PR 02 FE: gửi `turnId`, hiển thị deadline server, resume/retry đúng turn | FE interview | 2–3 ngày làm việc | Sau contract PR 02 + 3 ngày |
| PR 03: LLM thật + scoring theo rubric đã pin | BE AI/Interview | Ước lượng sau spike provider | Chốt sau spike |
| PR 03 FE: hiển thị follow-up và trạng thái AI đang xử lý/lỗi có thể retry | FE interview | 1–2 ngày làm việc | Sau contract PR 03 + 2 ngày |
| PR 04: voice theo lượt/storage/STT/TTS | BE AI + hạ tầng | Ước lượng sau spike codec/storage | Chốt sau spike |
| PR 04 FE: ghi âm, upload, phát TTS/audio với giới hạn codec/duration | FE interview | 3–5 ngày làm việc | Chốt sau contract audio |
| PR 05: streaming/SignalR/reconnect | BE Interview | Ước lượng sau contract FE | Chốt sau contract |
| PR 05 FE: partial transcript, chunk sequence, reconnect và phục hồi GET | FE interview | Ước lượng sau thử streaming | Chốt sau thử streaming |
| PR 06: PostgreSQL E2E/CI/tài liệu | BE + QA | 2–4 ngày làm việc sau PR 05 | Đề xuất PR 05 + 4 ngày làm việc |
| PR 06 FE: E2E Keycloak → voice → review → feedback/stats và sửa UI theo kết quả | FE + QA | 1–2 ngày làm việc sau tích hợp | Đề xuất PR 05 + 4 ngày làm việc |

T0 là ngày bắt đầu sprint do team chốt. Các owner là **vai trò đề xuất**, chưa
phân công cá nhân; mốc tương đối và thời lượng không phải cam kết. PO/tech lead
cần chuyển chúng thành ngày lịch và người phụ trách trước khi nhận PR.
**Demo tối thiểu (BE + FE):** F1-A1, PR 01–02 với text/mock; FE gửi `turnId`,
resume đúng bài, lưu nháp/xác nhận điểm và chỉ hiện feedback student sau duyệt;
lecturer xem statistics/CSV đã duyệt. **Hoàn chỉnh:**
PR 03–06 với provider thật, voice/streaming và kiểm thử PostgreSQL E2E.

Mỗi PR có test phù hợp và build pass trước khi sang mốc tiếp theo. Các regression
tests của F6 và HTTP contract tests duyệt điểm hiện có phải tiếp tục pass;
PR 06 mở rộng đến luồng voice hoàn chỉnh trên PostgreSQL thật.

Code F6 hiện đã nằm trong repo. Tích hợp trên HEAD mới nhất và kiểm tra regression
F6 sau mỗi mốc; không cần thao tác với commit baseline cũ `fc0a39c`.

F3 chỉ đánh dấu Done khi:

- [ ] Tất cả hành vi ở mục 1 đã có implementation và test.
- [ ] LLM/STT/TTS và storage thật chạy được với cấu hình triển khai.
- [ ] Partial transcript, câu hỏi realtime, reconnect và tự hết giờ đã nghiệm thu.
- [ ] Duyệt điểm đúng quyền, đúng range, atomic và idempotent.
- [ ] Bài thi mới đi hết luồng F3 → duyệt → F6 mà không sửa DB thủ công.
- [ ] Có hướng dẫn nâng cấp schema, cấu hình và chạy local cho đội/FE.

## Tài liệu kỹ thuật

- [ASP.NET Core SignalR](https://learn.microsoft.com/en-us/aspnet/core/signalr/introduction?view=aspnetcore-8.0)
- [BackgroundService và hosted services](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/host/hosted-services?view=aspnetcore-8.0)
- Tài liệu API/codec/streaming chính thức của provider sẽ được bổ sung sau bước readiness.
