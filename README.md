# Usage Quest 2 (C# 버전)

Claude Code의 `/usage` 값(5시간 세션 · 주간 한도 · 리셋 시각)을 레트로 RPG 상태창처럼 보여주는 Windows 위젯.
1.x(Electron)와 기능은 같고, 훨씬 가볍게 다시 만든 버전.

| | 1.x (Electron) | 2.x (C# WinForms) |
|---|---|---|
| 설치파일 | 약 94MB | 약 0.9MB |
| 실행 파일 | 수백 MB 폴더 | 5MB 파일 하나 (글꼴 포함) |
| 필요한 것 | 없음 | .NET Framework 4.8 (Windows 10/11 기본 포함) |

## 기능
- 위젯: 간단 모드(게이지만) / 전체 모드(캐릭터·대사·리셋 시각). 작업표시줄·Alt+Tab에 안 뜸, 드래그로 옮기면 위치 기억
- 작업표시줄 칸: `HP`(세션) / `STA`(주간 전체) 두 줄, 사용 %·리셋까지 남은 시간. 작업표시줄 창 안에 끼워 넣음(실패하면 겹쳐 띄우기)
  - 클릭 = 위젯 열기/닫기, 우클릭 = 메뉴, 왼쪽 점 손잡이 = 좌우 이동
- 트레이 아이콘: 캐릭터 얼굴이 상태(쌩쌩/지침/기절)에 따라 바뀜. 우클릭 메뉴에서 모든 설정
- 3분마다 자동 확인, 수동 확인 20초 쿨다운, 429 받으면 10분 대기
- **토큰 자동 갱신**: 토큰이 만료되면 Claude Code(`claude`)를 창 없이 잠깐 실행해 갱신 (30분에 한 번까지 자동, "토큰 갱신" 버튼으로 수동)
  - 1단계 `claude auth status` (모델 호출 없음). 로그인이 풀린 상태(`loggedIn:false`)면 여기서 멈추고 /login 안내
  - 안 되면 2단계 `claude -p "." --model haiku --safe-mode --no-session-persistence --tools ""` (가장 작은 모델로 짧은 요청 1번, 사용량 아주 조금, 훅·MCP·대화 기록 안 남김)
  - 트레이 메뉴 "토큰 만료 시 자동 갱신"으로 끄기
  - claude 위치: `%USERPROFILE%\.local\bin\claude.exe`, `%APPDATA%\npm\claude.cmd`, PATH 순서로 찾음 (WSL 안의 claude는 지원 안 함)
- **자동 업데이트**: 앱이 켜지고 20초 뒤 한 번, 그리고 트레이 메뉴 "업데이트 확인"을 눌렀을 때 [GitHub Releases](https://github.com/moshead5/usage-quest/releases)에서 새 버전 확인 → 위젯 제목줄 `UP` 버튼 / 트레이 알림 / 트레이 메뉴 "업데이트 설치"로 바로 설치 (화면 없이 설치 후 자동 재실행)
  - 받은 파일은 크기와 SHA-256(릴리스 설명의 `sha256:` 줄)으로 검증
  - 트레이 메뉴 "업데이트 확인"으로 수동 확인, "시작할 때 업데이트 확인"으로 시작 시 확인 끄기
- **Antigravity 사용량**: Google Antigravity가 켜져 있으면 모델 그룹별(Gemini / Claude·GPT) 5시간·주간 남은 양을 위젯 아래 칸에 표시
  - **Antigravity 앱(IDE)**: 앱이 PC 안에서 띄우는 언어 서버(127.0.0.1)에 앱 화면과 같은 요청을 보내 읽음. Google 로그인 정보는 쓰지 않음
  - **Antigravity CLI(agy)**: 트레이 메뉴 "Antigravity CLI(agy) 연결"을 한 번 누르면 `~/.gemini/antigravity-cli/settings.json`에 공식 상태줄(statusLine) 설정을 추가. agy가 상태가 바뀔 때마다 남은 사용량을 넘겨주고, 위젯은 마지막 값을 "agy · N분 전"과 함께 표시 (기존 설정은 `.usagequest.bak`으로 백업)
  - Antigravity가 꺼져 있으면 "꺼져 있음"으로 표시, 트레이 메뉴 "Antigravity 사용량 표시"로 끄기
  - 비공식 방식이라 Antigravity 업데이트로 바뀔 수 있음
- 단축키: `R`/`F5` 다시 확인, `Esc` 숨기기
- 첫 실행 시 "컴퓨터 시작 시 자동 실행" 켜짐 (트레이 메뉴에서 끄기)

## 설치
`UsageQuest-Setup-2.4.0.exe` 실행. 관리자 권한 필요 없음.
- 1.x(Electron) 버전이 깔려 있으면 설치 중에 자동으로 지우고 자동 실행 항목도 정리함
- 서명이 없어서 "Windows의 PC 보호" 창이 뜨면 **추가 정보 → 실행**
- 설치 위치: `%LOCALAPPDATA%\Programs\UsageQuest`, 설정: `%APPDATA%\UsageQuest\settings.json`
- 문제가 생기면 `%APPDATA%\UsageQuest\error.log` 확인

## 작동 방식
1. Claude Code 로그인 토큰 `%USERPROFILE%\.claude\.credentials.json`을 **읽기만** 함 (`CLAUDE_CONFIG_DIR` 지원, 못 찾으면 "파일 직접 지정")
2. `GET https://api.anthropic.com/api/oauth/usage` 호출 (비공식 엔드포인트 — 바뀌면 "알 수 없는 응답" 표시)
3. 계정 이메일은 `~/.claude.json`의 로그인 정보, 없으면 `/api/oauth/profile`
4. 토큰은 저장·갱신·전송하지 않음. api.anthropic.com 외에는 통신하지 않음
5. 의미가 확인된 항목(5시간, 주간 전체, 주간 Opus, 주간 Sonnet)만 표시

## 빌드
- 리눅스: `sudo apt install mono-complete nsis` → `./build.sh` → `makensis installer.nsi`
- Windows: Visual Studio 없이도 `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`로 빌드 가능 (`build.bat`)
- 가짜 데이터로 화면 보기: `UsageQuest.exe --demo=fresh` (`tired`, `ko`, `nologin`, `expired`)

## 라이선스
코드 MIT / 글꼴 Galmuri, Press Start 2P (SIL OFL 1.1)

## 새 버전 배포 (개발자용)
1. `src/Program.cs`의 `AssemblyVersion`, `installer.nsi`의 `VERSION` 올리기
2. (선택) `notes/vX.Y.Z.md`에 바뀐 점 적기
3. main에 커밋·push → 그 버전이 아직 릴리스되지 않았으면 GitHub Actions가 설치파일을 빌드해 Releases에 `vX.Y.Z`로 올림 (SHA-256 자동 기록)
4. 앱에서 "업데이트 확인" → 새 버전 설치
