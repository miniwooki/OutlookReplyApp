# 음수 부피(negative volume) 오류
요소가 과도하게 왜곡되면 negative volume in solid element 오류로 해석이 중단됩니다.
메시 품질을 개선하고, *CONTROL_TIMESTEP 의 TSSFAC 를 0.9 에서 0.6~0.8 로 낮추거나, *CONTROL_SOLID 의 ERODE 옵션을 검토합니다.
