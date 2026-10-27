# Ba task song song — Minh, Phúc, Khang

Phân công này **không gắn thời lượng hoặc deadline**. Có đúng ba task, mỗi
người sở hữu một task. **F3 do Minh và Phúc cùng triển khai**; Khang phụ trách
F1 question bank, FE F7/F6 và kiểm thử tích hợp, không nhận một PR triển khai
F3. Chi tiết nghiệp vụ theo [plan F1](F1-Implementation-Plan.md) và
[plan F3](F3-Implementation-Plan.md). Đường dẫn BE thuộc repo này; đường dẫn
FE thuộc repo `ai-oral-exam-web`.

| ID | Owner | Vùng chính | Phần F3 |
| --- | --- | --- | --- |
| **TASK-M** | **Minh** | Platform rubric/F7, vòng đời và review attempt | PR 01, PR 02, PR 05: review, deadline/idempotency, realtime |
| **TASK-P** | **Phúc** | Sinh câu F1, AI/voice và FE interview | PR 03, PR 04: LLM scoring, audio/STT/TTS |
| **TASK-K** | **Khang** | F1 bank/import, FE F1/F7/F6, CI và E2E | Kiểm thử/tích hợp contract; không sở hữu code nghiệp vụ F3 |

## TASK-M — Minh: rubric và F3 review/lifecycle/realtime

**Sở hữu file:** `database/`, AccessConfig rubric/publish/contract,
`InterviewService.cs`, `Interview/Application/Features/ScoreReview/`, worker
deadline và hub/event F3. Minh là người ghép cuối các adapter của Phúc vào
`InterviewService.cs` và `InterviewModule.cs`.

- [ ] F1-A1: platform rubric version bất biến, seed/migration dữ liệu cũ,
  pin version khi publish; `IExamConfigurationApi` trả cùng criteria/max cho
  mọi câu của session mới, session cũ vẫn đọc điểm theo rubric lịch sử.
- [ ] **F3 PR 01:** GET review chỉ đọc, lưu/chỉnh draft `ScoreReview Pending`,
  confirm Approved + Finalized, giữ PUT một bước cũ; kiểm tra quyền, range,
  retry, transaction/concurrency và privacy trước duyệt. Minh nối FE
  `score-review/` vào contract này.
- [ ] **F3 PR 02:** `turnId`, submit idempotent, deadline theo server,
  resume/retake, worker kết thúc attempt; không tạo turn/điểm trùng khi retry
  hoặc hai request cùng tới. Thống nhất schema với Phúc trước khi thêm trạng
  thái AI pending/retry.
- [ ] **F3 PR 05:** SignalR/event, thứ tự chunk và reconnect/GET phục hồi.
  Minh làm hub/orchestration; Phúc bàn giao payload audio/transcript và nối
  màn interview. Event không chứa điểm AI chưa duyệt.
- [ ] Cung cấp contract private object storage và cấu hình dùng chung cho
  tài liệu F1/audio F3; Phúc triển khai pipeline audio theo interface này.

**Done:** session mới pin rubric chung; một bài text/mock đi từ start đến
review/feedback không lộ điểm sớm; retry/deadline đúng; realtime phục hồi
không mất hoặc nhân đôi turn; migration PostgreSQL giữ nguyên bài lịch sử.

## TASK-P — Phúc: sinh câu F1 và AI/voice F3

**Sở hữu file:** subfolder/API generation F1 mới,
`Interview/Application/Ai/`, adapter LLM/STT/TTS và FE `interview/`. Phúc
không sửa `InterviewService.cs` hoặc SQL song song với Minh; gửi interface,
DTO và yêu cầu schema để Minh tích hợp.

- [ ] Chọn/thử provider LLM/STT/TTS với câu hỏi và audio mẫu; ghi structured
  output, codec/ngôn ngữ, latency/quota và cấu hình credential.
- [ ] F1-C: generator mock/deterministic tạo Draft từ câu mẫu/topic/Bloom;
  sau đó ingest PDF/PPTX private, retrieval theo course và RAG có citation
  kiểm chứng. Dùng bank API của Khang và rubric contract của Minh; không tạo
  hoặc sửa rubric, không auto-approve câu AI.
- [ ] **F3 PR 03:** LLM phân tích câu trả lời, hỏi phụ theo quota và chấm
  từng criterion của rubric version đã pin. Validate output/điểm, lưu trạng
  thái xử lý và retry; provider lỗi không làm mất transcript hoặc chấm 0.
- [ ] **F3 PR 04:** audio upload theo lượt, private storage qua contract của
  Minh, STT transcript và TTS câu hỏi; FE ghi âm/phát, hiển thị follow-up,
  partial transcript và phục hồi sau reconnect theo event contract của Minh.
- [ ] Kiểm tra regression F6 với điểm pending/confirmed, statistics/CSV;
  smoke test provider thật và loại credential/audio/tài liệu riêng tư khỏi log.

**Done:** câu AI từ nguồn đúng course có citation và đi qua lecturer duyệt;
một bài voice dùng provider thật có transcript, follow-up và gợi ý điểm hợp
lệ; lecturer vẫn quyết định điểm cuối.

## TASK-K — Khang: F1 bank, FE cấu hình/báo cáo và kiểm thử

**Sở hữu file:** subfolder/API question bank và import mới trong AccessConfig;
FE `src/api/`, `exam-config/`, `reporting/`; test contract/E2E và CI. Minh giữ
`database/` và publish command; Khang gửi schema bank và attach contract để
Minh đưa vào migration/publish mà không sửa cùng file.

- [ ] F1-A2/B: bank Draft/revision/approve/reject, attach câu Approved cùng
  course vào session Draft, import JSON atomic với lỗi theo dòng; câu đã
  publish giữ snapshot khi bank item đổi.
- [ ] FE question bank: list/edit/import, xem nguồn/câu AI Draft, duyệt/loại
  và chọn câu Approved. FE exam setup bỏ nhập rubric từng câu, có view rubric
  chung chỉ đọc theo contract Minh.
- [ ] Đối chiếu route/payload FE–BE, nối Keycloak và các màn F1/F7/F6 với API
  thật; thống nhất phạm vi đăng ký/Google SSO với Minh. Khang giữ shared API
  client, Minh/Phúc cập nhật các màn F3 của họ qua client đó.
- [ ] Thiết lập CI, test HTTP contract và E2E trên PostgreSQL/Keycloak:
  publish → thi text/voice → lecturer review → student feedback →
  statistics/CSV. Thử sai quyền, hết giờ, retry, ngắt mạng và restart worker;
  báo lỗi cho Minh/Phúc sửa theo module.

**Done:** bank chỉ attach câu Approved, import không lưu nửa chừng; FE F1/F7/F6
gọi BE thật với `VITE_USE_MOCKS=false`; E2E có bằng chứng tái hiện và test xanh.

## Bàn giao để ba task bắt đầu song song

1. **Minh → Phúc:** rubric/session DTO, criterion IDs/max, storage interface,
   review/turn/event contract. Phúc bắt đầu bằng fixture rubric và fake storage
   trước khi migration/hub thật được merge.
2. **Khang → Phúc/Minh:** bank Draft/Approved API và request attach; Minh giữ
   publish, Phúc ghi Draft AI qua API. Khang có thể dựng bank từ contract đã
   thống nhất trong khi Minh làm rubric migration.
3. **Phúc → Minh:** analyzer/scorer/audio interface, trạng thái `pending/failed`,
   codec và payload transcript. Minh nối các adapter vào orchestration sau khi
   PR adapter merge; hai người không sửa `InterviewService.cs` đồng thời.
4. **Khang → cả nhóm:** bảng route FE–BE và kết quả E2E. Khang kiểm thử F3,
   nhưng lỗi F3 được giao Minh hoặc Phúc theo phần sở hữu ở trên.

**Cổng tích hợp:** demo text/mock cần rubric, bank, lifecycle và review;
hoàn chỉnh cần thêm RAG, provider/voice, realtime và PostgreSQL E2E. Các
contract trên là điểm đồng bộ để merge, không phải lý do chờ toàn bộ task
của người khác hoàn thành mới bắt đầu.
