# 계산 발산·수렴 문제
residual 이 줄지 않거나 발산하면 under-relaxation 계수를 낮춥니다(압력 0.3, 운동량 0.7 → 0.5).
초기화는 hybrid initialization 을 권장합니다.
메시 skewness 가 0.95 이상이거나 orthogonal quality 가 0.1 미만인 셀이 있으면 메시를 개선합니다.

# 역류(reversed flow) 경고
출구 경계에서 reversed flow 경고가 나오면 출구를 하류로 연장하거나 backflow 조건을 확인합니다.
