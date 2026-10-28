package com.camorip.slider;

import android.content.Context;
import java.util.HashMap;
import java.util.Map;
import java.util.Locale;

final class SliderText {
    private static final Map<String, String[]> labels = new HashMap<>();
    static {
        labels.put("Connect", new String[] {"连接", "連線", "接続", "연결", "Connecter"});
        labels.put("Disconnect", new String[] {"断开", "中斷", "切断", "연결 해제", "Déconnecter"});
        labels.put("Settings", new String[] {"设置", "設定", "設定", "설정", "Paramètres"});
        labels.put("Connection", new String[] {"连接", "連線", "接続", "연결", "Connexion"});
        labels.put("Slider", new String[] {"滑条", "滑條", "スライダー", "슬라이더", "Curseur"});
        labels.put("Appearance", new String[] {"外观", "外觀", "外観", "모양", "Apparence"});
        labels.put("Controls", new String[] {"控制", "控制", "操作", "컨트롤", "Commandes"});
        labels.put("About", new String[] {"关于", "關於", "情報", "정보", "À propos"});
        labels.put("Apply", new String[] {"应用", "套用", "適用", "적용", "Appliquer"});
        labels.put("Cancel", new String[] {"取消", "取消", "キャンセル", "취소", "Annuler"});
        labels.put("Hide", new String[] {"隐藏", "隱藏", "隠す", "숨기기", "Masquer"});
        labels.put("Ready", new String[] {"就绪", "就緒", "準備完了", "준비됨", "Prêt"});
        labels.put("Connected", new String[] {"已连接", "已連線", "接続済み", "연결됨", "Connecté"});
        labels.put("Disconnected", new String[] {"已断开", "已中斷", "未接続", "연결 끊김", "Déconnecté"});
        labels.put("Waiting for PC", new String[] {"等待电脑", "等待電腦", "PCを待機中", "PC 대기 중", "En attente du PC"});
        labels.put("PC address", new String[] {"电脑地址", "電腦位址", "PCアドレス", "PC 주소", "Adresse du PC"});
        labels.put("Port", new String[] {"端口", "連接埠", "ポート", "포트", "Port"});
        labels.put("Heartbeat rate", new String[] {"心跳频率", "心跳頻率", "ハートビート頻度", "하트비트 주기", "Fréquence de maintien"});
        labels.put("Connect on launch", new String[] {"启动时连接", "啟動時連線", "起動時に接続", "시작 시 연결", "Connexion au démarrage"});
        labels.put("Sensor layout", new String[] {"传感器布局", "感應器配置", "センサー配置", "센서 배치", "Disposition des capteurs"});
        labels.put("Touch overlap", new String[] {"触摸重叠", "觸控重疊", "タッチの重なり", "터치 겹침", "Chevauchement tactile"});
        labels.put("Play area height", new String[] {"操作区域高度", "操作區域高度", "操作エリアの高さ", "입력 영역 높이", "Hauteur de la zone"});
        labels.put("Mirror input", new String[] {"镜像输入", "鏡像輸入", "入力を反転", "입력 좌우 반전", "Inverser horizontalement"});
        labels.put("Swap sensor rows", new String[] {"交换感应行", "交換感應列", "センサー行を交換", "센서 행 전환", "Inverser les rangées"});
        labels.put("Haptic feedback", new String[] {"触觉反馈", "觸覺回饋", "触覚フィードバック", "햅틱 피드백", "Retour haptique"});
        labels.put("Sensor grid", new String[] {"感应网格", "感應網格", "センサーグリッド", "센서 격자", "Grille des capteurs"});
        labels.put("Input diagnostics", new String[] {"输入诊断", "輸入診斷", "入力診断", "입력 진단", "Diagnostic des entrées"});
        labels.put("Low visual load", new String[] {"低视觉负载", "低視覺負載", "表示負荷を軽減", "표시 부하 줄이기", "Affichage allégé"});
        labels.put("Theme", new String[] {"主题", "主題", "テーマ", "테마", "Thème"});
        labels.put("System", new String[] {"跟随系统", "跟隨系統", "システム", "시스템", "Système"});
        labels.put("Dark", new String[] {"深色", "深色", "ダーク", "어둡게", "Sombre"});
        labels.put("Light", new String[] {"浅色", "淺色", "ライト", "밝게", "Clair"});
        labels.put("Dynamic color", new String[] {"动态颜色", "動態色彩", "ダイナミックカラー", "동적 색상", "Couleurs dynamiques"});
        labels.put("Accent color", new String[] {"强调色", "強調色", "アクセントカラー", "강조 색상", "Couleur d’accent"});
        labels.put("Game LED colors", new String[] {"游戏灯光颜色", "遊戲燈光色彩", "ゲームのLED色", "게임 LED 색상", "Couleurs LED du jeu"});
        labels.put("Preset LED", new String[] {"预设灯光", "預設燈光", "LEDプリセット", "LED 프리셋", "Préréglage LED"});
        labels.put("LED brightness", new String[] {"灯光亮度", "燈光亮度", "LEDの明るさ", "LED 밝기", "Luminosité LED"});
        labels.put("Background opacity", new String[] {"背景不透明度", "背景不透明度", "背景の不透明度", "배경 불투명도", "Opacité du fond"});
        labels.put("Language", new String[] {"语言", "語言", "言語", "언어", "Langue"});
        labels.put("Import background", new String[] {"导入背景", "匯入背景", "背景を読み込む", "배경 가져오기", "Importer un fond"});
        labels.put("Clear background", new String[] {"清除背景", "清除背景", "背景を消去", "배경 지우기", "Effacer le fond"});
        labels.put("Import skin", new String[] {"导入皮肤", "匯入外觀", "スキンを読み込む", "스킨 가져오기", "Importer un thème"});
        labels.put("Export settings", new String[] {"导出设置", "匯出設定", "設定を書き出す", "설정 내보내기", "Exporter les paramètres"});
        labels.put("Card access code", new String[] {"卡片访问码", "卡片存取碼", "カードアクセスコード", "카드 액세스 코드", "Code d’accès de carte"});
        labels.put("Insert card", new String[] {"刷卡", "刷卡", "カードを読み取る", "카드 읽기", "Présenter la carte"});
        labels.put("Insert coin", new String[] {"投币", "投幣", "コイン投入", "코인 투입", "Insérer une pièce"});
        labels.put("Source code", new String[] {"源代码", "原始碼", "ソースコード", "소스 코드", "Code source"});
        labels.put("Reset settings", new String[] {"重置设置", "重設設定", "設定をリセット", "설정 초기화", "Réinitialiser"});
        labels.put("Reset", new String[] {"重置", "重設", "リセット", "초기화", "Réinitialiser"});
    }

    static String get(Context context, String text) {
        String tag = context.getSharedPreferences("slider", Context.MODE_PRIVATE).getString("language", "");
        Locale locale = tag.isEmpty() ? Locale.getDefault() : Locale.forLanguageTag(tag);
        int index = switch (locale.getLanguage()) { case "zh" -> locale.getCountry().equals("TW") || locale.getCountry().equals("HK") ? 1 : 0;
            case "ja" -> 2; case "ko" -> 3; case "fr" -> 4; default -> -1; };
        String[] translated = labels.get(text); return index < 0 || translated == null ? text : translated[index];
    }
}
