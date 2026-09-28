# 라이선스 서버 연결 실패
FlexNet Licensing error -15,10 은 라이선스 서버(ansyslmd, lmgrd)에 연결할 수 없다는 뜻입니다.
1055, 2325 포트와 벤더 데몬 포트가 방화벽에서 열려 있는지, ANSYSLMD_LICENSE_FILE 환경 변수가 1055@서버 형식인지 확인합니다.
LS-DYNA 는 LSTC_LICENSE_SERVER 환경 변수를 사용합니다.
