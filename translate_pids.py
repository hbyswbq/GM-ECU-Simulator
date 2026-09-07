#!/usr/bin/env python3
"""
Translate PID library friendly_name column to Chinese.
Decrypts AES-128-CBC + gzip CSV, translates friendly_name, re-encrypts.
"""
import gzip
import os
from Crypto.Cipher import AES

KEY = bytes([0x6F, 0x8B, 0x3D, 0xA2, 0x4C, 0x71, 0xE5, 0x5D, 0xB0, 0x88, 0x19, 0x2C, 0x7E, 0xAA, 0x04, 0xD3])
IV  = bytes([0x3A, 0x09, 0xC5, 0x7F, 0x91, 0x64, 0xB8, 0x22, 0x0E, 0xF5, 0x6D, 0xAB, 0x50, 0x1A, 0x47, 0xCC])

# Chinese translation mapping for common A2L PID names (a2l_name -> Chinese friendly_name)
# This covers the most common/important PIDs; technical identifiers not in this map keep their A2L name.
PID_TRANSLATIONS = {
    # Engine running / status
    "VeEPSI_b_EngineRunning": "发动机运行状态",
    # Engine load
    "VeMAFC_Pct_EngLoadJ1979": "发动机负荷百分比",
    # Coolant
    "SfECTI_T_EngCoolCvrtd": "发动机冷却液温度",
    "SfECTI_T_EngCoolCvrtdJ1979": "发动机冷却液温度(J1979)",
    # Fuel correction
    "VaFCLS_K_ShortTermFuelCrtn[CiFADR_FuelBank1]": "短期燃油修正(缸组1)",
    "VaFCLS_K_ShortTermFuelCrtn[CiFADR_FuelBank2]": "短期燃油修正(缸组2)",
    "VaFCLL_K_LTM_Current[CiFADR_FuelBank1]": "长期燃油修正(缸组1)",
    "VaFCLL_K_LTM_Current[CiFADR_FuelBank2]": "长期燃油修正(缸组2)",
    # Fuel system
    "SfFSSY_Stat_FuelSysMode": "燃油系统状态",
    # RPM
    "VeRPMR_n_EngSpdCvrtdJ1979": "发动机转速(RPM)",
    "VeRPMR_n_EngSpdCvrtd": "发动机转速",
    # Vehicle speed
    "VeVSSW_n_VehSpdCvrtdJ1979": "车辆速度",
    "VeVSSW_n_VehSpdCvrtd": "车辆速度",
    # Spark / timing
    "SfSPK_D_SparkAdvCvrtdJ1979": "点火提前角",
    "SfSPK_D_SparkAdvCvrtd": "点火提前角",
    # Intake air
    "SfIATS_T_AirIntakeTmpCvrtd": "进气温度",
    "SfIATS_T_AirIntakeTmpCvrtdJ1979": "进气温度(J1979)",
    "VeMAF_M_AirFlowCvrtdJ1979": "空气质量流量",
    "VeMAF_M_AirFlowCvrtd": "空气质量流量",
    "SfMAP_K_IntakeManifoldAbsPress": "进气歧管绝对压力",
    "SfMAP_K_IntakeManifoldAbsPressJ1979": "进气歧管绝对压力(J1979)",
    # Throttle
    "VeTPS_Pct_ThrPosCvrtdJ1979": "节气门位置",
    "VeTPS_Pct_ThrPosCvrtd": "节气门位置",
    "VeTPAC_Pct_ThrPosACmdCvrtd": "节气门执行器控制位置",
    # O2 sensors
    "SfO2S_V_O2Snsr1Cvrtd[CiO2SR_O2Snsr1]": "氧传感器1电压",
    "SfO2S_V_O2Snsr2Cvrtd[CiO2SR_O2Snsr2]": "氧传感器2电压",
    "SfO2S_V_O2Snsr3Cvrtd[CiO2SR_O2Snsr3]": "氧传感器3电压",
    "SfO2S_V_O2Snsr4Cvrtd[CiO2SR_O2Snsr4]": "氧传感器4电压",
    # EGR
    "VeEGR_Pct_EgrCmdCvrtdJ1979": "EGR阀指令",
    "VeEGR_Pct_EgrErrCvrtdJ1979": "EGR误差",
    # EVAP
    "VeEVAP_Pct_EvapPurgeCmdCvrtdJ1979": "蒸发排放吹扫指令",
    # Fuel level
    "SfFLVL_Pct_FuelLvlCvrtdJ1979": "燃油液位",
    "SfFLVL_Pct_FuelLvlCvrtd": "燃油液位",
    # Barometric
    "SfBARO_K_BaroPressCvrtdJ1979": "大气压力",
    "SfBARO_K_BaroPressCvrtd": "大气压力",
    # Catalyst temp
    "SfCAT_T_CatTempB1S1CvrtdJ1979": "催化器温度(缸组1传感器1)",
    "SfCAT_T_CatTempB2S1CvrtdJ1979": "催化器温度(缸组2传感器1)",
    # VIN
    "BaVINF_y_OdoVIN[CeVINR_i_0doVIN_Char01]": "车辆识别码(VIN)",
    # MIL / DTC
    "SfMIL_b_MilLmpReq": "故障指示灯(MIL)请求",
    "SfDTC_Cnt_StorDtc": "已存储故障码计数",
    # Time / distance
    "VeTIMR_t_TimeSinceEngStartCvrtd": "发动机启动后时间",
    "VeDIST_K_DistSinceClrCvrtdJ1979": "清除故障码后行驶距离",
    "VeTIMR_t_TimeSinceClrCvrtdJ1979": "清除故障码后时间",
    # Oil
    "SfOILT_T_OilTempCvrtd": "机油温度",
    # Fuel pressure
    "SfFUEL_K_FuelRailPressCvrtd": "燃油轨压力",
    "SfFUEL_K_FuelRailPressCvrtdJ1979": "燃油轨压力(J1979)",
    # Torque
    "VeTRQ_Nm_IndTorqueCvrtd": "指示扭矩",
    "VeTRQ_Nm_EngRefTorqueCvrtd": "发动机参考扭矩",
    # Boost
    "VeMAP_K_BoostPressCvrtd": "增压压力",
    # Transmission
    "SfTRNS_n_TransGearCvrtd": "变速箱档位",
    "SfTFT_T_TransFluidTmpCvrtd": "变速箱油温",
    # Battery
    "SfBATT_V_BattVoltCvrtd": "蓄电池电压",
    # Ambient
    "SfAAT_T_AmbAirTmpCvrtd": "环境空气温度",
    # A/C
    "SfACPS_Pct_ACPressCvrtd": "空调压力",
    "SfACON_b_ACClutchReq": "空调离合器请求",
    # Cruise
    "SfCCON_b_CruiseCtrlOn": "巡航控制开启",
    # Brake
    "SfBRK_B_BrakePedalPos": "制动踏板位置",
    # Steering
    "SfSTR_A_SteeringWheelAngle": "方向盘角度",
    # Yaw / accel
    "SfYAW_R_YawRate": "横摆率",
    "SfLAT_G_LateralAccel": "横向加速度",
    "SfLON_G_LongAccel": "纵向加速度",
}

def decrypt_library(path):
    with open(path, 'rb') as f:
        ciphertext = f.read()
    cipher = AES.new(KEY, AES.MODE_CBC, IV)
    decrypted = cipher.decrypt(ciphertext)
    pad_len = decrypted[-1]
    decrypted = decrypted[:-pad_len]
    return gzip.decompress(decrypted).decode('utf-8')

def encrypt_library(csv_text, path):
    compressed = gzip.compress(csv_text.encode('utf-8'))
    # PKCS7 padding
    pad_len = 16 - (len(compressed) % 16)
    compressed += bytes([pad_len] * pad_len)
    cipher = AES.new(KEY, AES.MODE_CBC, IV)
    ciphertext = cipher.encrypt(compressed)
    with open(path, 'wb') as f:
        f.write(ciphertext)

def split_csv_line(line):
    """Simple CSV splitter that handles quoted fields."""
    result = []
    current = []
    in_quotes = False
    for char in line:
        if char == '"':
            in_quotes = not in_quotes
        elif char == ',' and not in_quotes:
            result.append(''.join(current))
            current = []
        else:
            current.append(char)
    result.append(''.join(current))
    return result

def translate_library(path):
    csv_text = decrypt_library(path)
    lines = csv_text.strip().split('\n')
    header = lines[0]
    translated = 0

    new_lines = [header]
    for line in lines[1:]:
        if not line.strip():
            new_lines.append(line)
            continue
        cols = split_csv_line(line)
        if len(cols) < 6:
            new_lines.append(line)
            continue

        friendly_name = cols[4].strip()
        a2l_name = cols[5].strip()

        # Only translate if friendly_name is empty
        if not friendly_name and a2l_name in PID_TRANSLATIONS:
            cols[4] = PID_TRANSLATIONS[a2l_name]
            translated += 1
            # Reconstruct CSV line
            new_line = ','.join(cols)
            new_lines.append(new_line)
        else:
            new_lines.append(line)

    new_csv = '\n'.join(new_lines) + '\n'
    encrypt_library(new_csv, path)
    return translated

def main():
    base = '/home/user/.super_doubao/super-doubao-runtime/workspace/GM-ECU-Simulator/Common/Pids'
    libraries = ['Mode01Library.bin', 'Mode1ALibrary.bin', 'Mode22Library.bin', 'Mode22LibraryFord.bin']

    for lib in libraries:
        path = os.path.join(base, lib)
        count = translate_library(path)
        print(f'{lib}: translated {count} PID friendly names')

if __name__ == '__main__':
    main()
