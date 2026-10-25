# Task Khang — đếm bài đang làm, phiên hết giờ và kiểm thử tích hợp

## Mục tiêu và phạm vi

Tài liệu này chuyển phần việc của Khang trong ảnh phân công thành checklist có thể thực hiện. Chỉ tập trung vào:

- **B5:** thêm `inProgressCount` vào danh sách phiên thi.
- **B6:** xử lý/hiển thị phiên thi đã quá `EndTime`.
- **C7:** bỏ `using` trùng trong `InterviewResultsApi.cs`.
- **D10–D12:** kiểm thử tích hợp các luồng người dùng/Keycloak, cập nhật hồ sơ và chốt điểm; kiểm tra ghi chú liên quan script `03_...sql` nếu nằm trong phạm vi được giao.

Không làm các mục của Minh: `UserService.cs`, `AiConfigService.cs`, `AuditLogService.cs`, `CourseService.cs`, `AdminCoursesController.cs` và các thay đổi A1–A4, B4, C8–C9. Không thay đổi SQL trong phạm vi này.

## Đối chiếu code hiện tại

- `ExamSessionSummaryDto` đang có `ParticipantCount`, `TakenCount`, `ToReviewCount`, nhưng **chưa có `InProgressCount`**.
- `IInterviewResultsApi.GetAttemptCountsAsync` trả `AttemptCounts(TakenCount, ToReviewCount)`. `InterviewResultsApi` tính `TakenCount` từ `PendingReview` hoặc `Finalized`, và `ToReviewCount` từ `PendingReview`; do đó bài `InProgress` hiện chưa được đếm trong danh sách phiên.
- `ExamSessionQueries.GetSessionsAsync` đã ghép participant/attempt counts vào DTO. Test tương ứng đã có, nhưng fake hiện chỉ trả hai bộ đếm.
- `InterviewResultsApi.cs` hiện có hai dòng `using AiOralExam.Modules.Interview.Contracts;` giống nhau; C7 là dọn đúng dòng lặp này.
- Phiên thi có các trạng thái lưu trữ `Draft`, `Published`, `Closed`. Đóng phiên thủ công hiện có trong `ExamSessionCommands.CloseAsync`; `ExamSessionInfo.IsOpenAt` cũng đã kiểm tra khoảng thời gian `StartTime`–`EndTime`, và luồng bắt đầu bài từ chối phiên không mở. Chưa thấy cơ chế tự cập nhật trạng thái lưu trữ thành `Closed` chỉ vì thời gian kết thúc đã qua.
- **B6 chưa đủ rõ để tự chọn cách triển khai:** “tự đóng” có thể là ghi `Closed` vào DB, còn “trả trạng thái thực tế” có thể là trả trạng thái hiệu lực khi đọc mà không sửa dữ liệu. Ảnh cũng nhắc hỏi Minh về cách đóng phiên hiện tại. Cần thống nhất với Minh trước khi sửa logic hoặc API contract.

## File dự kiến thay đổi

### B5 — `inProgressCount`

Các file code/test dự kiến:

1. [`src/Modules/Interview/AiOralExam.Modules.Interview.Contracts/IInterviewResultsApi.cs`](src/Modules/Interview/AiOralExam.Modules.Interview.Contracts/IInterviewResultsApi.cs)  
   Mở rộng `AttemptCounts` với `InProgressCount`, giữ nguyên ý nghĩa hiện tại của `TakenCount` và `ToReviewCount`.
2. [`src/Modules/Interview/AiOralExam.Modules.Interview/Application/InterviewResultsApi.cs`](src/Modules/Interview/AiOralExam.Modules.Interview/Application/InterviewResultsApi.cs)  
   Tính số attempt có trạng thái `InProgress` theo `ExamSessionId`; vẫn trả đủ bộ ba số đếm bằng 0 cho session không có attempt. Dọn `using` trùng ở đây (C7).
3. [`src/Modules/AccessConfig/AiOralExam.Modules.AccessConfig/Application/ExamSessions/ExamSessionDtos.cs`](src/Modules/AccessConfig/AiOralExam.Modules.AccessConfig/Application/ExamSessions/ExamSessionDtos.cs)  
   Thêm `InProgressCount` vào `ExamSessionSummaryDto`; đảm bảo JSON list trả thuộc tính `inProgressCount`.
4. [`src/Modules/AccessConfig/AiOralExam.Modules.AccessConfig/Application/ExamSessions/ExamSessionQueries.cs`](src/Modules/AccessConfig/AiOralExam.Modules.AccessConfig/Application/ExamSessions/ExamSessionQueries.cs)  
   Gán count mới từ contract vào kết quả mỗi phiên; không làm thay đổi lọc quyền lecturer/admin, lọc `courseId`, thứ tự hoặc các count cũ.
5. [`tests/AiOralExam.UnitTests/InterviewResultsApiTests.cs`](tests/AiOralExam.UnitTests/InterviewResultsApiTests.cs)  
   Bổ sung/điều chỉnh dữ liệu và assertion cho `InProgress`, `PendingReview`, `Finalized` và session không có attempt.
6. [`tests/AiOralExam.UnitTests/ExamSessionQueriesTests.cs`](tests/AiOralExam.UnitTests/ExamSessionQueriesTests.cs)  
   Cập nhật fake contract và xác nhận count mới được ghép đúng vào DTO, cùng các count cũ.

### B6 — phiên quá hạn

Đã xác nhận chọn **đóng trạng thái lưu trữ** (`Published` → `Closed`) sau `EndTime`.

- Background service trong AccessConfig kiểm tra mỗi 15 giây và dùng một câu lệnh cập nhật có điều kiện (`Published` và `EndTime <= UTC now`), tránh cập nhật lại phiên đã đóng hoặc phiên Draft.
- `EndTime` là mốc không bao gồm: ngay tại `EndTime`, session không còn mở để bắt đầu attempt mới.
- List/detail đọc trạng thái đã lưu; có thể chậm tối đa một chu kỳ kiểm tra (15 giây) mới phản ánh `Closed`.
- Tác vụ hệ thống không ghi DB audit log vì schema bắt buộc `user_id` và không có actor hệ thống được xác nhận; thay vào đó ghi application log khi có session được đóng. Không giả mạo lecturer/admin làm actor.
- Đã thêm unit tests để kiểm tra chọn Published session trước/đúng mốc hết giờ và biên `IsOpenAt`. Cần kiểm tra cập nhật SQL thực tế/luồng API đầu cuối trong môi trường test có .NET SDK và PostgreSQL.

### C7 — dọn using

- `InterviewResultsApi.cs`: xóa đúng một trong hai dòng `using AiOralExam.Modules.Interview.Contracts;` bị lặp.
- Không tiện tay dọn các using/file khác ngoài phần được giao.

## Phối hợp để tránh xung đột với Minh

1. Trước khi bắt đầu code, kiểm tra working tree và đồng bộ nhánh `feature-crud` mới nhất theo quy trình nhóm; ảnh ghi nhận Minh đang sửa `ExamSessionQueries.cs` trên nhánh `b55378d`.
2. Trao đổi trực tiếp với Minh để biết thay đổi nào đã vào nhánh chung và thống nhất ai chỉnh phần nào trong `ExamSessionQueries.cs`. B6 được thực hiện bằng hosted service riêng, không thay đổi truy vấn list.
3. Không lấy bản `ExamSessionQueries.cs` cũ ghi đè lên phần Minh đã làm. Tích hợp thay đổi trên phiên bản mới nhất, giữ nguyên các xử lý Minh sở hữu.
4. Hạn chế vùng thay đổi của Khang ở phần ghép count và logic B6 đã được đồng thuận. Các file `InterviewResultsApi.cs`/tests là phần tương đối độc lập, nhưng vẫn cần cập nhật trên nhánh mới nhất.
5. Mỗi người tạo nhánh/PR riêng theo hướng dẫn trong ảnh; khi review, kiểm tra diff chỉ chứa phần được giao.

## Kế hoạch kiểm thử và tiêu chí đạt

### Kiểm thử tự động B5/C7

Chạy các test liên quan trong `AiOralExam.UnitTests` (tối thiểu `InterviewResultsApiTests` và `ExamSessionQueriesTests`):

- Session không có attempt: `TakenCount = 0`, `ToReviewCount = 0`, `InProgressCount = 0`.
- Một attempt `InProgress`: chỉ tăng `InProgressCount`.
- Một attempt `PendingReview`: tăng `TakenCount` và `ToReviewCount`, không tăng `InProgressCount`.
- Một attempt `Finalized`: tăng `TakenCount`, không tăng `ToReviewCount` hay `InProgressCount`.
- Trộn các trạng thái: mỗi count đúng theo định nghĩa, không cộng trùng bài `InProgress` vào `TakenCount`.
- List phiên trả đúng `inProgressCount`; count participant, số câu hỏi, filter course và quyền lecturer/admin vẫn giữ nguyên.
- Contract/fake trong test phải khớp chữ ký `AttemptCounts` mới; build project/test phải không lỗi.

### Kiểm thử B6 sau khi chốt thiết kế

- Unit test: session Published trước hoặc đúng `EndTime` đủ điều kiện đóng; session hết hạn nhưng Draft/đã Closed và session Published còn hạn không bị chọn.
- Unit test: session mở ngay trước `EndTime`; không mở tại hoặc sau mốc đó.
- Với PostgreSQL test riêng: xác nhận worker cập nhật Published thành Closed, chạy lặp an toàn, không ảnh hưởng Draft/Closed/còn hạn; kiểm tra cập nhật đồng thời với thao tác đóng thủ công.
- API test: list/detail phản ánh Closed sau lần kiểm tra worker; sinh viên bắt đầu attempt bị từ chối tại/sau `EndTime` với `exam_session_not_open`.
- Xác nhận application log có số lượng session tự đóng; không kỳ vọng DB audit record có actor người dùng.

### Kiểm thử thủ công D10–D12 (không thêm code nếu chỉ là phần kiểm thử)

Chuẩn bị môi trường local/test đã cấu hình Keycloak. Kiểm tra cấu hình client `aives-admin-api` được nhắc trong ảnh và cấu hình `KeycloakAdmin` trước khi thử; không đưa client secret vào tài liệu, log hoặc commit.

1. Dùng tài khoản administrator tạo user thật qua `POST /api/admin/users`; xác nhận user được tạo và ID Keycloak khớp ID user API, vai trò/các mã Student/Lecturer hợp lệ.
2. Gọi `POST /api/admin/users/{id}/setup-link`; xác nhận API báo gửi thành công và email/link đặt mật khẩu được Keycloak xử lý theo cấu hình môi trường.
3. Đăng nhập bằng user đó, gọi `PATCH /api/auth/me` với `fullName` hợp lệ; xác nhận profile đổi, trường khác không bị sửa và request không có token bị từ chối.
4. Dùng attempt đang `PendingReview`, đăng nhập lecturer được phân công, gọi `PUT /api/interview-attempts/{id}/score-review`; xác nhận score/review được lưu và trạng thái thành `Finalized`. Thử lại để xác nhận không chốt lặp/không đi ngược state machine.
5. Ảnh có nhắc script `03_...sql`: chỉ xác nhận với người giao việc xem đây là yêu cầu kiểm thử hay thay đổi. Không sửa script trong scope Khang; nếu được yêu cầu kiểm tra migration, chỉ dùng môi trường test riêng và theo quy trình áp dụng DB của nhóm, không chạy trên dữ liệu dùng chung.

**Hoàn tất khi:** B5/C7/B6 có test tự động xanh; B6 được kiểm tra với PostgreSQL/API; D10–D12 có ghi nhận kết quả thao tác thực tế (ẩn email, token, secret và dữ liệu nhạy cảm); diff không chứa thay đổi của Minh hay thay đổi SQL ngoài phạm vi.

## Trạng thái thực hiện

- **B5:** đã thêm `InProgressCount` vào contract, kết quả truy vấn và DTO danh sách; đã cập nhật test cho trạng thái trộn/không có attempt.
- **C7:** đã xóa dòng `using` trùng trong `InterviewResultsApi.cs`.
- **Tài liệu API:** README đã mô tả `inProgressCount`.
- **B6:** đã thêm background auto-close 15 giây/lần với điều kiện UTC, cập nhật có điều kiện và quy tắc `EndTime` không bao gồm; README mô tả hành vi và audit/log. Đã thêm unit tests cho tiêu chí hết hạn và ranh giới thời gian.
- **Kiểm thử tự động:** chưa chạy được vì môi trường không có .NET SDK; người dùng không cho phép cài SDK. Cần chạy test sau khi chọn môi trường có .NET 8 SDK.
- **D10–D12:** chưa chạy các luồng API thật vì chúng tạo user/gửi email/cập nhật dữ liệu; cần môi trường test riêng và thông tin test do nhóm cung cấp. Repository đã có unit tests dùng fake cho tạo user/setup email (`UserCreationTests`, `KeycloakAdminClientTests`), cập nhật hồ sơ (`UserCreationTests`) và chốt điểm (`InterviewScoreReviewTests`); chúng chưa thay thế cho kiểm thử tích hợp thật và chưa được chạy trong lượt này.
- Không chạy SQL hoặc thao tác database do quy định an toàn của workspace; không sửa schema/script.
- Không có thay đổi SQL.
