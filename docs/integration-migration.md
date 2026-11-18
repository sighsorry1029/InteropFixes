# InteropFixes 통합 — 2026-11-07

**2026-11-09 후속 변경:** 장식 효과 최적화 설정을 제거하고, 이후 InteropFixes 전체의 필수 설치·ServerSync 검사를 제거했다. 일반 호환 패치는 클라이언트만으로 사용할 수 있고, 장식 효과 생략은 v2 전용 합의로 서버와 참여자의 지원이 확인된 기능에만 적용한다. [현재 자동 적용 정책](automatic-cosmetics.md)을 참고한다. 아래 이행·배포 기록은 최초 통합과 후속 단계의 이력이다.

## 책임 분리

- Blasted는 vanilla Valheim runtime fix로 유지한다. 일반 Character/StatusEffect/
  EnemyHud 수명 정리, F2 폰트 수정, 액체·씬·지형·서버·런타임 최적화는 남는다.
- MyPitsDontLeak/Scenic 감지는 Blasted 자신의 중복 처리를 피하기 위한 조건이므로
  Blasted에 유지한다.
- 최신 Blasted의 MagicPlugin 수정, VneiItemLookupFix, EpicLootCraftyBoxesBridge는
  InteropFixes의 별도 모듈로 통합한다.
- vanilla 메서드에 붙은 훅이라도 모드 연동에만 필요한 InventoryGrid/UITooltip,
  Container.Load/Save, ZNet.StopAll 처리는 해당 통합 모듈에 함께 둔다.

## 보존과 이행

기존 InteropFixes의 일반 캐릭터 수정은 최신 Blasted에 존재하므로 새 InteropFixes에서
제거했다. 기존 InteropFixes만 사용하던 구성에는 기능 범위 변경이 있다.
기존 미커밋 Character 수정과 교체 전 소스/프로젝트는
`refer/legacy-interop-20261107`에 보관했다. 이 자료는 컴파일·패키징 대상이 아니다.
기존 VNEI/Bridge 소스 프로젝트는 수정하거나 삭제하지 않았다.

최초 통합 단계에서 로컬 Steam 게임에 Blasted/InteropFixes Debug DLL을 배치하고
SHA-256 일치를 확인했다. 이 기록은 아래 ServerSync·기능 합의 후속 변경의 최종
DLL 검증과 구분한다. 기존 VneiItemLookupFix.dll과 EpicLootCraftyBoxesBridge.dll은
`refer/installed-standalone-20261107`로 옮겨 중복 로드를 방지했다.
이동 전후 해시와 원래 위치는 그 폴더의 `backup-manifest.json`에 있다.
Gale 프로필은 변경하지 않았고 실행 중 게임을 종료/재시작하지 않았다.
후속 ServerSync·기능 합의 변경의 InteropFixes 최종 Debug 빌드·병합·로컬 배치도
완료했으며, 일치한 최종 SHA-256과 재실행한 검사 범위는 `Tests/README.md`에 기록했다.

각 모듈은 별도 Harmony 소유자를 사용한다. VNEI/Bridge의 외부 모드 타입은
실행 시 검증한 어댑터로 접근하며, 선택 모드 DLL이 없을 때도 통합 DLL의 타입을
읽을 수 있다. 한 모듈의 초기화/정리 실패가 다른 모듈의 훅을 해제하지 않는다.
알려진 독립 플러그인이 설치되어 있거나 구 소유자의 훅이 남아 있으면 해당
새 모듈만 양보한다. 구 플러그인의 부분 초기화도 중복 설치로 덮어쓰지 않는다.
임의의 실행 중 DLL 교체·후발 플러그인 로딩은 지원하지 않는다.

MagicPlugin 외부 패치 복원은 새 대체 패치의 제거를 확인한 뒤에만 수행한다.
기존 ZDO marker 키와 launch/Nova 값은 유지한다. 장식 효과 후속 변경에서는
native `RPC_OnHit`를 재생 신호로 재해석하거나 broadcast로 바꾸지 않는다.
일반 캐릭터 tracker에 연결되었던 작은 HUD 정리만 Magic 모듈 내부로 분리했다.

## 서버·클라이언트 구성 변경 이력

이전 후속 요청에서는 InteropFixes 자체를 서버·접속 클라이언트 모두의 필수 모드로
변경했다. 고정된 공통 `valheim-1.0.7-r1` ServerSync를 vendor하고 최종 DLL에
internalize 병합했다. 별도의 ServerSync 플러그인은 배치하지 않았다.
출처·해시·라이선스는 `Libs/ServerSync.provenance.md`에 있다.
MagicPlugin/VNEI/EpicLoot/AzuCraftyBoxes/Blasted의 선택적 의존성은 그대로이며,
전용 서버에 UI 연동 대상까지 설치하라는 뜻은 아니다.

이전 단계에서는 `MagicPlugin.EnableNetworkCosmeticOptimization=true`를
서버에서 동기화하고 기존 OFF 값을 보존했다. 2026-11-09에는 이 옵션과 설정 변경
이벤트·정책 분기를 제거했다. 현재는 cfg 항목이 없으며, 예전 값이 파일에 남아 있어도
읽지 않는다. 자동 적용 정책은 서버의 기능 합의 허가를 우회하지 않는다.
DLL 교체에는 재시작이 필요하다.

같은 날 후속 요청으로 ServerSync 참조·병합·초기 동기화 훅과 전체 필수 설치 검사를
제거했다. 현재 일반 호환 수정은 상대편 InteropFixes 설치를 요구하지 않는다.
장식 효과 생략은 서버와 참여자들의 v2 지원 합의가 있을 때만 적용하며, 없는 서버나
미설치·미지원 참여자가 있는 세션에서는 원래 효과 생성 경로를 유지한다.
구 바이너리의 자체 필수 설치 검사는 새 DLL이 해제할 수 없다.

## 수신 준비와 기능 합의

모듈이 사용 가능한 경우 수신 훅을 준비한다. 고정 ON 정책만으로
FX ZDO를 곧바로 생략하지 않는다. 네이티브 피어 접속 승인, 해당 씬의 실제 prefab 검사,
준비된 참가자들의 기능 교집합을 확인한 뒤 새 생략을 허용한다. 구 Blasted 훅 때문에
Magic 모듈이 양보하거나 개별 prefab 검사가 실패하면 해당 지원 정보도 합의에 반영된다.
동일 DLL 설치를 실제 기능 준비 완료와 동일시하지 않는다.

연결·접속 정보·씬 준비/파괴·기능 변화·접속 해제 경계에서 작은 상태 보고와
revision/nonce를 교환하고 결과를 캐시한다. 매 프레임 prefab/IL 분석이나 RTT에 따른
동적 최적화는 추가하지 않았다. 미확인 접속자가 있으면 서버의 허용 mask를 0으로
낮추고, 현재 세션·연결의 유효한 준비 보고가 모인 뒤 상향 허가한다.
합의 프로토콜과 Hello/Report/Grant RPC 이름은 v2로 구분하며, 유효한 보고가 없는
참여자에게 Grant를 반복 전송하지 않는다. 응답이 없는 참여자를 합의 대상에서 제외하지 않는다.
이는 기존 클라이언트가 하향 허가를 수신했다는 ACK 장벽은 아니다.
선택 연동의 실패만으로 일반
게임 접속·저장·다른 모듈을 차단하지 않는다.

이미 기록된 launch/Nova carrier와 실제로 생략을 완료한 이벤트의 수신은 현재의
새 작업 허가 mask와 분리한다. 명중·Arctic iceblock 파괴는 실제 효과를 local-only로
생성한 뒤 세션과 원본 객체 ID를 포함한 명시적 이벤트를 송신한다. 수신에서는
세션·참가자·기능·좌표·활성 구역 및 남아 있는 원본 객체의 소유권을 확인하고,
최근 4096개의 객체/기능 결정을 중복 제거한다. 원본 객체가 먼저 파괴돼도 이벤트를
처리할 수 있으며, 새 작업 허가를 잃었다는 이유만으로 이미 진행된 생략의 재생을 취소하지 않는다.

새 Arctic iceblock은 생성 시 사망 재생을 미리 확정하지 않는다. 기존 Arctic 표식도
사망 FX 생략의 단독 증거로 사용하지 않는다. 예를 들어 gamepad 수신자 지정 경로는
원래 네트워크 FX를 유지하므로 사전 표식만 읽으면 효과가 중복될 수 있기 때문이다.
launch/Nova 표식은 유지하고, native `RPC_OnHit`의 원래 전달과 처리는 유지한다.

## 비동기 전환의 한계

새 접속자를 감지하고 허가를 낮추어도 기존 클라이언트가 그 메시지를 받기 전까지
이전 허가로 효과를 생성할 수 있다. 이 합의는 게임 입장이나 ZDO 전달을 전역적으로
멈추는 원자적 장벽이 아니다. 기능이 다른 늦은 접속자가 이미 진행 중인 효과와
겹치는 경우까지 누락·동일 표시를 보장하지 않는다. 원본 FX와 재생 FX를 뒤늦게
둘 다 생성하는 보정도 하지 않는다. 실제 서버/클라이언트 조합의 렌더링과
멀티플레이 검증은 아직 별도로 필요하다.

## 검증 범위

원본 Valheim 1.0.21을 공통 기준으로 사용한다. 설치본 assembly_valheim.dll은
보관된 클라이언트 원본과 SHA-256이 같다:
`9F959C0396EC4240D47FCBE5D0D062C428CC627244506DB5EA567F9D77B2B769`.

실제 외부 DLL의 관련 타입·멤버·호출 흐름 검사, 격리 상태 검사와 최종 DLL의
Harmony 설치/복원 검사를 구분한다. 전체 DLL 해시·MVID는 실행 허용 조건이 아니다.
검사는 임의 코드의 의미상 동등성이나 실제 Unity 멀티플레이 동작의 증명이 아니다.
상세 결과와 재현 방법은 `Tests/README.md`에 기록한다.
