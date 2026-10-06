# ai-oral-exam-api

Backend của **AIVES – AI-powered Viva Exam System** (SWD392): hệ thống thi vấn đáp có AI hỏi, hỏi xoáy và gợi ý điểm theo rubric; giảng viên luôn là người chốt điểm cuối.

- **Kiến trúc:** ASP.NET Core 8 **modular monolith** — 1 API, 3 module nghiệp vụ, 1 database
- **Database:** PostgreSQL 16, truy cập bằng EF Core 8 (Npgsql). Schema do **SQL script** tạo (`database/`), *không* dùng EF migration
- **Auth:** email + mật khẩu (ASP.NET Core Identity `PasswordHasher`) → JWT có role `Student` / `Lecturer` / `Administrator`
- **AI:** STT / TTS / LLM qua interface; M1 dùng bản **mock** (chấm điểm cố định, chạy lại vẫn ra cùng kết quả)

Repo frontend: `ai-oral-exam-web`.

---

## 1. Cài đặt cần có

| Công cụ | Phiên bản | Kiểm tra |
|---|---|---|
| .NET SDK | 8.0.x | `dotnet --version` |
| Docker Desktop | bản mới | `docker compose version` |
| (tuỳ chọn) Visual Studio 2022 / Rider / VS Code + C# Dev Kit | | |

> Không cài PostgreSQL trên máy cũng được: Docker sẽ chạy PostgreSQL cho bạn.

## 2. Chạy nhanh (4 bước)

```bash
# 1) Lấy code
git clone https://github.com/SWD392-LAB/ai-oral-exam-api.git
cd ai-oral-exam-api

# 2) Dựng database (lần đầu tự chạy database/01_schema.sql rồi 02_seed.sql)
docker compose up -d
docker compose ps          # đợi postgres báo "healthy"

# 3) Chạy API
dotnet run --project src/AiOralExam.Api --launch-profile http

# 4) Mở Swagger
#    http://localhost:5080/swagger
```

Kiểm tra API đã nối được DB: mở http://localhost:5080/health → phải thấy `Healthy`.

Trong Visual Studio: mở `AiOralExam.sln`, chọn `AiOralExam.Api` làm startup project, chọn profile **http**, nhấn F5.

### Thử luồng thi M1 trên Swagger

1. `POST /api/auth/login` với `{ "email": "han.hg@aives.edu.vn", "password": "Password@123" }` → copy `accessToken`.
2. Bấm nút **Authorize** (góc trên phải), dán token (không cần gõ chữ `Bearer`).
3. `GET /api/interview/my-sessions` → thấy 3 phiên: 2 phiên đang mở (S1, S4) và 1 phiên chưa mở (S6).
4. `POST /api/interview-attempts` với `{ "examSessionId": "33333333-0000-0000-0000-000000000001" }` → `201`, nhận `attemptId` và câu hỏi đầu tiên (`currentTurn`).
5. `POST /api/interview-attempts/{attemptId}/responses` với `{ "answerText": "..." }` → nhận điểm và nhận xét của mock AI, cùng câu hỏi tiếp theo. Lặp lại tới khi `outcome = "Completed"`.

> Mỗi câu chỉ được trả lời trong `timeLimitPerQuestion` giây (S1 là 120s), cộng thêm 15s dự phòng. Nộp muộn hơn thì bị tính là hết giờ (`timedOut: true`), câu trả lời không được lưu.

Các request mẫu cũng có sẵn trong `src/AiOralExam.Api/AiOralExam.Api.http` (dùng với VS Code REST Client / Visual Studio).

## 3. Dữ liệu mẫu

**Mật khẩu chung: `Password@123`**

| Email | Role | Dùng để test |
|---|---|---|
| `admin@aives.edu.vn`, `tuan.dm@aives.edu.vn` | Administrator | xem mọi môn / phiên thi / báo cáo |
| `an.nv@aives.edu.vn` | Lecturer | SWD392 (S1, S3) |
| `mai.bt@aives.edu.vn` | Lecturer | SWD392 + DBI202 |
| `ha.dt@aives.edu.vn` | Lecturer | DBI202 (S4) |
| `long.nd@aives.edu.vn` | Lecturer | SWT301 (S5) |
| `nam.lq@aives.edu.vn` | Lecturer | PRN232 (S2, S6) |
| `binh.tt@aives.edu.vn` | Lecturer | Google SSO → **không** đăng nhập bằng mật khẩu được |
| `han.hg@aives.edu.vn` | Student | **chưa thi** – dùng để demo luồng thi |
| `chau.lm@aives.edu.vn` | Student | S1 đã thi, **điểm đã chốt** → xem được báo cáo |
| `dung.pq@aives.edu.vn` | Student | S1 đã thi, **chờ giảng viên duyệt** → chưa xem được báo cáo |
| `khoa.vt@aives.edu.vn` | Student | chưa đặt mật khẩu (token kích hoạt: `demo-setup-token-khoa`) |
| `duy.hg@aives.edu.vn` | Student | tài khoản **bị khoá** → đăng nhập thất bại |
| 36 sinh viên khác (`SE180005`…`SE180040`) | Student | xem bảng `users` |

| Phiên thi | Môn | Trạng thái | Câu hỏi | Ghi chú |
|---|---|---|---|---|
| S1 `33333333-0000-0000-0000-000000000001` | SWD392 | Published, **đang mở** | 3 | 9 lượt thi (đã chốt / chờ duyệt / đang thi dở) |
| S2 `…0002` | PRN232 | Draft | 4 | sinh viên không thấy |
| S3 `…0003` | SWD392 | Closed | 4 | 12 lượt thi – dùng để xem thống kê lớp |
| S4 `…0004` | DBI202 | Published, **đang mở** | 4 | 5 lượt thi, Hân đã được đăng ký |
| S5 `…0005` | SWT301 | Closed | 4 | 8 lượt thi |
| S6 `…0006` | PRN232 | Published, **chưa mở** (mở sau 3 ngày) | 3 | dùng để test lỗi `exam_session_not_open` |

Giờ mở của S1/S4/S6 tính theo `now()` lúc chạy seed, nên reset DB lúc nào cũng demo được.

**Reset dữ liệu về như ban đầu:**

```bash
docker compose down -v     # -v: xoá luôn volume dữ liệu
docker compose up -d       # chạy lại 01_schema.sql + 02_seed.sql
```

**Xem DB:** pgAdmin ở http://localhost:5050 (`admin@aives.edu.vn` / `admin123`) → Add server: host `postgres`, port `5432`, user `aives`, password `aives@123`. Hoặc dùng DBeaver / DataGrip với host `localhost:5432`.

## 4. Cấu trúc project

```
ai-oral-exam-api/
├── AiOralExam.sln
├── docker-compose.yml               # PostgreSQL 16 + pgAdmin
├── Directory.Build.props            # cấu hình chung: net8.0, nullable...
├── Directory.Packages.props         # version NuGet khai báo 1 chỗ (Central Package Management)
├── database/
│   ├── 01_schema.sql                # NGUỒN CHUẨN của schema (16 bảng)
│   └── 02_seed.sql                  # dữ liệu mẫu
├── src/
│   ├── AiOralExam.Api/              # Host: Program.cs, JWT, Swagger, CORS, middleware bắt lỗi
│   ├── Shared/
│   │   └── AiOralExam.SharedKernel/ # lỗi dùng chung (AppException, ApiError), Roles, ICurrentUser
│   └── Modules/
│       ├── AccessConfig/            # F7 - Access Control & Exam Configuration
│       │   ├── AiOralExam.Modules.AccessConfig.Contracts/  # interface + DTO public cho module khác
│       │   └── AiOralExam.Modules.AccessConfig/            # Domain, Infrastructure (DbContext), Application, Controllers
│       ├── Interview/               # F3 - AI Viva Interview
│       │   ├── AiOralExam.Modules.Interview.Contracts/
│       │   └── AiOralExam.Modules.Interview/
│       │       ├── Domain/          # Participant, InterviewAttempt (state machine), QuestionResponse, InterviewTurn...
│       │       ├── Application/     # InterviewService (điều phối luồng thi)
│       │       │   └── Ai/          # IAnswerAnalyzer, IRubricScorer, ISpeechToText, ITextToSpeech, ILlmService
│       │       │       └── Mock/    # bản mock cho M1
│       │       ├── Infrastructure/  # InterviewDbContext
│       │       └── Controllers/
│       └── Reporting/               # F6 - Feedback & Reporting (không có bảng, chỉ đọc)
└── tests/
    └── AiOralExam.UnitTests/        # xUnit
```

### Module và bảng dữ liệu

| Module | Làm gì | Sở hữu bảng | Được gọi |
|---|---|---|---|
| **AccessConfig** (F7) | tài khoản, đăng nhập, phân công giảng viên, phiên thi, câu hỏi, rubric, cấu hình AI, audit log | `users`, `roles`, `password_setup_tokens`, `courses`, `course_lecturers`, `exam_sessions`, `questions`, `rubrics`, `ai_service_configs`, `audit_logs` | — |
| **Interview** (F3) | chạy buổi thi, hỏi xoáy, AI gợi ý điểm, (M3) giảng viên duyệt | `participants`, `interview_attempts`, `question_responses`, `interview_turns`, `ai_evaluations`, `score_reviews` | `AccessConfig.Contracts` |
| **Reporting** (F6) | báo cáo sinh viên, kết quả, thống kê lớp, xuất bảng điểm | *(không có)* | `Interview.Contracts`, `AccessConfig.Contracts` |

**Ba quy tắc giữ ranh giới module:**

1. Mỗi module chỉ đọc/ghi **bảng của mình** (mỗi module có DbContext riêng, chỉ map bảng của nó).
2. Module khác chỉ được reference project `*.Contracts`, **không** reference thẳng project module. Ví dụ Interview muốn lấy câu hỏi thì gọi `IExamConfigurationApi`.
3. Reporting chỉ đọc điểm đã lưu, **không bao giờ gọi lại AI**.

### API hiện có

| Method | Route | Role | Task tracker |
|---|---|---|---|
| POST | `/api/auth/login` | – | BE-PLAT-07 |
| GET | `/api/auth/me` | mọi role | BE-PLAT-07 |
| GET | `/api/courses/mine` | Lecturer, Admin | |
| GET | `/api/exam-sessions`, `/api/exam-sessions/{id}` | Lecturer, Admin | |
| GET | `/api/interview/my-sessions` | Student | FE-INT-01 |
| POST | `/api/interview-attempts` | Student | BE-PLAT-04 |
| GET | `/api/interview-attempts/{id}` | Student | |
| POST | `/api/interview-attempts/{id}/responses` | Student | BE-AI-05 |
| GET | `/api/reports/attempts/{id}` | Student (chỉ khi điểm đã chốt), Lecturer, Admin | M3 |
| GET | `/api/reports/exam-sessions/{id}/results` | Lecturer, Admin | M3 |
| GET | `/api/reports/exam-sessions/{id}/statistics` | Lecturer, Admin | M3 |
| GET | `/api/reports/exam-sessions/{id}/grade-sheet` | Lecturer, Admin | M3 (CSV tạm, chờ mẫu của trường) |
| GET | `/health` | – | |

Mọi lỗi đều trả về cùng một format (FOUNDATION-02):

```json
{ "code": "exam_session_not_open", "message": "Phiên thi chưa được mở.", "traceId": "0HN...", "details": null }
```

FE nên `switch` theo `code`, còn `message` để hiện cho người dùng.

### Trạng thái lượt thi (BE-AI-01)

```
InProgress ──(trả lời xong câu cuối)──▶ PendingReview ──(giảng viên chốt điểm)──▶ Finalized
```

Không có chiều ngược lại. Mỗi lần nộp câu trả lời, API trả `outcome`:

- `FollowUp`: AI hỏi xoáy, `currentTurn.type = "FollowUp"`
- `NextQuestion`: chuyển sang câu chính tiếp theo
- `Completed`: hết câu, lượt thi chuyển sang `PendingReview`

## 5. Cấu hình (`appsettings.json`)

| Key | Mặc định | Ý nghĩa |
|---|---|---|
| `ConnectionStrings:Database` | `Host=localhost;Port=5432;Database=aives;Username=aives;Password=aives@123` | khớp `docker-compose.yml` |
| `Jwt:SigningKey` | đặt sẵn trong `appsettings.Development.json` | ≥ 32 ký tự. Môi trường thật đặt qua biến môi trường `Jwt__SigningKey` |
| `Jwt:ExpiresMinutes` | 480 | |
| `Interview:ShowAiScoreToStudent` | `true` | M1 cho sinh viên thấy điểm mock ngay. Từ M3 đặt `false` (chỉ thấy khi giảng viên đã chốt) |
| `Interview:Mock:EnableFollowUps` | `false` | bật lên để thử luồng hỏi xoáy với mock (câu trả lời < 15 từ → hỏi xoáy) |
| `Interview:AllowRetake` | `false` | cho thi lại hay không (open point) |
| `Interview:TimeLimitGraceSeconds` | 15 | số giây dự phòng sau khi hết giờ |
| `Cors:AllowedOrigins` | `http://localhost:5173`, `http://localhost:3000` | địa chỉ chạy FE |

Đổi cấu hình riêng trên máy mình mà không commit: dùng `dotnet user-secrets` (project đã có `UserSecretsId`) hoặc biến môi trường, ví dụ `ConnectionStrings__Database=...`.

## 6. Sửa database

Schema lấy **`database/01_schema.sql` làm chuẩn** (team chọn không dùng EF migration). Khi cần thêm hoặc sửa cột / bảng:

1. Sửa `database/01_schema.sql` (và `02_seed.sql` nếu dữ liệu mẫu bị ảnh hưởng).
2. Sửa entity trong `Domain/` và mapping trong `*DbContext.cs` của **đúng module sở hữu bảng**.
   - Tên property dạng PascalCase tự map sang snake_case: `TimeLimitPerQuestion` ↔ `time_limit_per_question`.
   - Enum được lưu dạng chuỗi, tên phải khớp `CHECK` constraint trong SQL.
3. Reset DB: `docker compose down -v && docker compose up -d`.
4. Báo cả team reset DB sau khi merge.

## 7. Test

```bash
dotnet build
dotnet test
```

Unit test hiện có: state machine của lượt thi, mock AI chạy ra kết quả cố định, cách chia nhóm điểm trong thống kê.

## 8. Lỗi hay gặp

| Triệu chứng | Cách xử lý |
|---|---|
| `docker compose up` báo port 5432 đã bị dùng | Máy đang chạy PostgreSQL khác. Đổi `"5432:5432"` thành `"5433:5432"` và sửa `Port=5433` trong connection string |
| Sửa file SQL nhưng DB không đổi | Script chỉ chạy khi volume còn trống → `docker compose down -v` rồi `up -d` |
| `/health` báo Unhealthy | Container chưa chạy hoặc chưa healthy: `docker compose ps`, `docker compose logs postgres` |
| API không khởi động, báo `Jwt:SigningKey phải dài tối thiểu 32 ký tự` | Đang chạy ngoài môi trường Development → đặt biến `Jwt__SigningKey` |
| `401 unauthenticated` trên Swagger | Chưa bấm **Authorize**, hoặc token đã hết hạn |
| `409 attempt_already_taken` | Sinh viên đó đã thi xong phiên này. Dùng `han.hg` hoặc reset DB |
| `409 exam_session_not_open` | Phiên chưa publish hoặc ngoài giờ mở (S6 là cố ý như vậy) |

## 9. Việc tiếp theo (theo tracker)

- **M2 – AI thích ứng:** thay `Mock*` trong `Interview/Application/Ai` bằng adapter thật (OpenAI / Gemini / Google TTS…), đổi đăng ký trong `InterviewModule.cs`; thêm API upload audio + WebSocket.
- **M3 – Báo cáo & duyệt điểm:** API cho giảng viên xem transcript và chốt điểm (`ScoreReview` → attempt `Finalized`); xuất bảng điểm theo mẫu của trường.
- **M4 – Access & cấu hình:** Google SSO, đặt/đặt lại mật khẩu qua link, CRUD phiên thi / câu hỏi / rubric, publish (khoá câu hỏi), quản lý tài khoản, cấu hình AI, ghi audit log.

Các chỗ cần làm đã được đánh dấu `TODO (M2/M3/M4)` trong code.

> Lưu ý: .NET 8 LTS được Microsoft hỗ trợ tới **10/11/2026**. Dự án vẫn chạy bình thường sau ngày đó; nếu cần nâng lên .NET 10 LTS thì chỉ sửa `TargetFramework` trong `Directory.Build.props` và version package trong `Directory.Packages.props`.
