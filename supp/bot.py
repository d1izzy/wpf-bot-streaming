import json, logging, os
from pathlib import Path
from typing import Any, Dict, Optional
from dotenv import load_dotenv
from telegram import KeyboardButton, ReplyKeyboardMarkup, ReplyKeyboardRemove, Update
from telegram.ext import CallbackContext, CommandHandler, Filters, MessageHandler, Updater

BASE_DIR = Path(__file__).resolve().parent
load_dotenv(BASE_DIR / ".env")
LOGS_DIR = BASE_DIR / "logs"; LOGS_DIR.mkdir(exist_ok=True)
SCENARIO_PATH = BASE_DIR / "scenario.json"
FAQ_PATH = BASE_DIR / "faq.json"
logging.basicConfig(level=logging.INFO, format="%(asctime)s - %(levelname)s - %(message)s", handlers=[logging.StreamHandler(), logging.FileHandler(LOGS_DIR / "bot.log", encoding="utf-8")])
log = logging.getLogger("botsupp")
TOKEN = os.getenv("BOT_TOKEN", "").strip()
PROXY_URL = os.getenv("BOTSUPP_PROXY_URL", "").strip() or os.getenv("ALL_PROXY", "").strip()
ADMIN_IDS = set()
for x in os.getenv("ADMIN_ID", "").replace(";", ",").split(","):
    x=x.strip()
    if x:
        try: ADMIN_IDS.add(int(x))
        except ValueError: log.warning("Некорректный ADMIN_ID: %s", x)
USER_STEP: Dict[int, str] = {}
WAITING: Dict[int, Any] = {}
ACTIVE: Dict[int, int] = {}
ADMIN_ACTIVE: Dict[int, int] = {}
DEFAULT = {"botName":"BotSupp Bot","startStepId":"main","unknownMessage":"Я не понял сообщение. Выберите действие из меню.","steps":[{"id":"main","text":"Здравствуйте! Выберите действие:","buttons":[{"text":"Информация","action":"message","message":"Это универсальный бот BotSupp Studio."},{"text":"Связаться с оператором","action":"operator"}]}]}

def load_json(path: Path) -> Optional[dict]:
    try:
        return json.loads(path.read_text(encoding="utf-8")) if path.exists() else None
    except Exception as e:
        log.error("Ошибка чтения %s: %s", path.name, e); return None

def faq_to_scenario(faq: dict) -> dict:
    buttons=[]; steps=[{"id":"main","text":"Здравствуйте! Выберите пункт:","buttons":buttons}]
    for key,item in faq.items():
        if not isinstance(item, dict): continue
        q=str(item.get("question","")).strip(); a=str(item.get("answer","")).strip()
        if not q or not a: continue
        sid=f"faq_{key}"; buttons.append({"text":q,"action":"go","target":sid})
        steps.append({"id":sid,"text":f"❓ {q}\n\n{a}","buttons":[{"text":"В меню","action":"go","target":"main"},{"text":"Оператор","action":"operator"}]})
    return {"botName":"FAQ bot","startStepId":"main","unknownMessage":"Выберите пункт из меню.","steps":steps} if buttons else DEFAULT

def scenario() -> dict:
    data=load_json(SCENARIO_PATH)
    if data and isinstance(data.get("steps"), list) and data["steps"]: return data
    faq=load_json(FAQ_PATH)
    return faq_to_scenario(faq) if isinstance(faq, dict) else DEFAULT

def step(sc: dict, sid: str) -> Optional[dict]:
    return next((s for s in sc.get("steps",[]) if s.get("id")==sid), None)

def keyboard(st: dict):
    rows=[[KeyboardButton(str(b.get("text")))] for b in st.get("buttons",[]) if b.get("text")]
    return ReplyKeyboardMarkup(rows or [[KeyboardButton("В меню")]], resize_keyboard=True)

def send_step(update: Update, sid: str):
    sc=scenario(); st=step(sc, sid) or step(sc, sc.get("startStepId"))
    if not st: update.message.reply_text("Сценарий не настроен.", reply_markup=ReplyKeyboardRemove()); return
    USER_STEP[update.effective_user.id]=st["id"]; update.message.reply_text(st.get("text",""), reply_markup=keyboard(st))

def start(update: Update, context: CallbackContext): send_step(update, scenario().get("startStepId","main"))
def help_cmd(update: Update, context: CallbackContext): update.message.reply_text("Используйте /start для меню.")

def operator(update: Update, context: CallbackContext):
    u=update.effective_user; WAITING[u.id]=u.id
    for aid in ADMIN_IDS:
        try: context.bot.send_message(aid, f"📩 Запрос к оператору от {u.first_name or ''} @{u.username or 'не указан'} ID {u.id}\nПринять: /take_{u.id}")
        except Exception as e: log.error("admin notify failed: %s", e)
    update.message.reply_text("✅ Запрос отправлен оператору.")

def take(update: Update, context: CallbackContext):
    aid=update.effective_user.id
    if aid not in ADMIN_IDS: return
    try: uid=int(update.message.text.split("_",1)[1])
    except Exception: update.message.reply_text("Команда: /take_123456789"); return
    ACTIVE[uid]=aid; ADMIN_ACTIVE[aid]=uid; WAITING.pop(uid,None)
    update.message.reply_text(f"Чат с {uid} открыт. /close чтобы закрыть."); context.bot.send_message(uid,"✅ Оператор подключился.")

def close(update: Update, context: CallbackContext):
    aid=update.effective_user.id; uid=ADMIN_ACTIVE.pop(aid, None)
    if not uid: update.message.reply_text("Активного чата нет."); return
    ACTIVE.pop(uid,None); update.message.reply_text("Чат закрыт."); context.bot.send_message(uid,"Диалог с оператором завершён.")

def text(update: Update, context: CallbackContext):
    uid=update.effective_user.id; msg=update.message.text.strip()
    if uid in ADMIN_IDS and uid in ADMIN_ACTIVE: context.bot.send_message(ADMIN_ACTIVE[uid], f"💬 Ответ оператора:\n\n{msg}"); update.message.reply_text("✅ Отправлено."); return
    if uid in ACTIVE: context.bot.send_message(ACTIVE[uid], f"💬 Сообщение от {uid}:\n\n{msg}"); update.message.reply_text("✅ Передано оператору."); return
    sc=scenario(); st=step(sc, USER_STEP.get(uid, sc.get("startStepId","main"))) or step(sc, sc.get("startStepId","main"))
    for b in st.get("buttons",[]):
        if msg != b.get("text"): continue
        act=str(b.get("action","message")).lower()
        if act=="go": send_step(update, b.get("target", sc.get("startStepId","main"))); return
        if act=="operator": operator(update, context); return
        if act=="start": send_step(update, sc.get("startStepId","main")); return
        if act=="end": USER_STEP.pop(uid,None); update.message.reply_text(b.get("message","Диалог завершён."), reply_markup=ReplyKeyboardRemove()); return
        update.message.reply_text(b.get("message","Действие выполнено."), reply_markup=keyboard(st)); return
    update.message.reply_text(sc.get("unknownMessage","Выберите действие из меню."), reply_markup=keyboard(st))

def main():
    if not TOKEN: log.error("BOT_TOKEN не задан в .env рядом с bot.py"); return
    kwargs={"connect_timeout":20,"read_timeout":20}
    if PROXY_URL: kwargs["proxy_url"]=PROXY_URL; log.info("Прокси: %s", PROXY_URL)
    up=Updater(token=TOKEN, use_context=True, request_kwargs=kwargs)
    dp=up.dispatcher; dp.add_handler(CommandHandler("start", start)); dp.add_handler(CommandHandler("help", help_cmd)); dp.add_handler(CommandHandler("close", close)); dp.add_handler(MessageHandler(Filters.regex(r"^/take_\d+$"), take)); dp.add_handler(MessageHandler(Filters.text & ~Filters.command, text))
    log.info("Бот запущен из %s", BASE_DIR); up.start_polling(); up.idle()
if __name__ == "__main__": main()
