import os
import json
import logging
from pathlib import Path
from typing import Any, Dict, List, Optional

from dotenv import load_dotenv
from telegram import Update, KeyboardButton, ReplyKeyboardMarkup, ReplyKeyboardRemove
from telegram.ext import Updater, CommandHandler, MessageHandler, Filters, CallbackContext

# Автономная рабочая папка бота.
# Один и тот же bot.py можно копировать в разные папки:
# %LOCALAPPDATA%/BotSuppRuntime/bots/<bot_id>/bot.py
BASE_DIR = Path(__file__).resolve().parent
ENV_PATH = BASE_DIR / ".env"
BOT_CONFIG_PATH = BASE_DIR / "bot_config.json"
SCENARIO_PATH = BASE_DIR / "scenario.json"
FAQ_PATH = BASE_DIR / "faq.json"  # legacy fallback
LOGS_DIR = BASE_DIR / "logs"
LOGS_DIR.mkdir(exist_ok=True)

load_dotenv(ENV_PATH)

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s - %(levelname)s - %(message)s",
    handlers=[
        logging.StreamHandler(),
        logging.FileHandler(LOGS_DIR / "bot.log", encoding="utf-8"),
    ],
)
logger = logging.getLogger("botsupp")

BOT_TOKEN = os.getenv("BOT_TOKEN", "").strip()
PROXY_URL = os.getenv("BOTSUPP_PROXY_URL", "").strip() or os.getenv("ALL_PROXY", "").strip()
RAW_ADMIN_IDS = os.getenv("ADMIN_ID", "").strip()


def parse_admin_ids(raw: str) -> set[int]:
    result: set[int] = set()
    for part in raw.split(","):
        part = part.strip()
        if not part:
            continue
        try:
            result.add(int(part))
        except ValueError:
            logger.warning("Некорректный ADMIN_ID пропущен: %s", part)
    return result


ADMIN_IDS = parse_admin_ids(RAW_ADMIN_IDS)
USER_STEPS: Dict[int, str] = {}
WAITING_OPERATOR: Dict[int, Dict[str, Any]] = {}
ACTIVE_CHATS: Dict[int, int] = {}  # user_id -> admin_id
ADMIN_ACTIVE_USER: Dict[int, int] = {}  # admin_id -> user_id


DEFAULT_SCENARIO = {
    "botName": "BotSupp Bot",
    "startStepId": "main",
    "unknownMessage": "Я не понял сообщение. Выберите действие из меню.",
    "steps": [
        {
            "id": "main",
            "text": "Здравствуйте! Выберите действие:",
            "buttons": [
                {"text": "Информация", "action": "message", "message": "Это универсальный Telegram-бот, настроенный через BotSupp Studio."},
                {"text": "Связаться с оператором", "action": "operator"},
            ],
        }
    ],
}


def load_json(path: Path) -> Optional[Dict[str, Any]]:
    if not path.exists():
        return None
    try:
        with path.open("r", encoding="utf-8") as f:
            data = json.load(f)
        return data if isinstance(data, dict) else None
    except Exception as ex:
        logger.error("Ошибка чтения %s: %s", path.name, ex)
        return None


def legacy_faq_to_scenario(faq: Dict[str, Any]) -> Dict[str, Any]:
    buttons = []
    steps = [{"id": "main", "text": "Здравствуйте! Выберите интересующий пункт:", "buttons": buttons}]
    for key, item in faq.items():
        if not isinstance(item, dict):
            continue
        question = str(item.get("question", "")).strip()
        answer = str(item.get("answer", "")).strip()
        if not question or not answer:
            continue
        step_id = f"faq_{key}"
        buttons.append({"text": question, "action": "go", "target": step_id})
        steps.append(
            {
                "id": step_id,
                "text": f"❓ {question}\n\n{answer}",
                "buttons": [
                    {"text": "В меню", "action": "go", "target": "main"},
                    {"text": "Связаться с оператором", "action": "operator"},
                ],
            }
        )
    if not buttons:
        return DEFAULT_SCENARIO
    return {"botName": "FAQ bot", "startStepId": "main", "unknownMessage": "Выберите пункт из меню.", "steps": steps}


def load_scenario() -> Dict[str, Any]:
    scenario = load_json(SCENARIO_PATH)
    if scenario:
        return normalize_scenario(scenario)
    faq = load_json(FAQ_PATH)
    if faq:
        return normalize_scenario(legacy_faq_to_scenario(faq))
    return DEFAULT_SCENARIO


def normalize_scenario(scenario: Dict[str, Any]) -> Dict[str, Any]:
    steps = scenario.get("steps")
    if not isinstance(steps, list) or not steps:
        return DEFAULT_SCENARIO
    clean_steps = []
    ids = set()
    for step in steps:
        if not isinstance(step, dict):
            continue
        step_id = str(step.get("id", "")).strip()
        text = str(step.get("text", "")).strip()
        if not step_id or not text:
            continue
        buttons = step.get("buttons", [])
        clean_buttons = []
        if isinstance(buttons, list):
            for b in buttons:
                if not isinstance(b, dict):
                    continue
                label = str(b.get("text", "")).strip()
                action = str(b.get("action", "message")).strip().lower()
                if label:
                    clean_buttons.append({**b, "text": label, "action": action})
        clean_steps.append({**step, "id": step_id, "text": text, "buttons": clean_buttons})
        ids.add(step_id)
    if not clean_steps:
        return DEFAULT_SCENARIO
    start = str(scenario.get("startStepId", "")).strip()
    if start not in ids:
        start = clean_steps[0]["id"]
    return {
        "botName": str(scenario.get("botName", "BotSupp Bot")),
        "startStepId": start,
        "unknownMessage": str(scenario.get("unknownMessage", "Я не понял сообщение. Выберите действие из меню.")),
        "steps": clean_steps,
    }


def get_step(scenario: Dict[str, Any], step_id: str) -> Optional[Dict[str, Any]]:
    for step in scenario.get("steps", []):
        if step.get("id") == step_id:
            return step
    return None


def make_keyboard(step: Dict[str, Any]) -> ReplyKeyboardMarkup:
    rows = [[KeyboardButton(str(button.get("text", "")))] for button in step.get("buttons", []) if button.get("text")]
    if not rows:
        rows = [[KeyboardButton("В меню")]]
    return ReplyKeyboardMarkup(rows, resize_keyboard=True, one_time_keyboard=False)


def send_step(update: Update, step_id: str) -> None:
    scenario = load_scenario()
    step = get_step(scenario, step_id) or get_step(scenario, scenario["startStepId"])
    if not step:
        update.message.reply_text("Сценарий не настроен.", reply_markup=ReplyKeyboardRemove())
        return
    USER_STEPS[update.effective_user.id] = step["id"]
    update.message.reply_text(step["text"], reply_markup=make_keyboard(step))


def notify_admins(context: CallbackContext, user: Any) -> None:
    if not ADMIN_IDS:
        return
    WAITING_OPERATOR[user.id] = {
        "id": user.id,
        "name": user.first_name or "Пользователь",
        "username": user.username or "не указан",
    }
    text = (
        "📩 Новый запрос к оператору\n\n"
        f"Имя: {user.first_name or 'Не указано'}\n"
        f"Username: @{user.username or 'не указан'}\n"
        f"ID: {user.id}\n\n"
        f"Чтобы принять чат, отправьте команду: /take_{user.id}"
    )
    for admin_id in ADMIN_IDS:
        try:
            context.bot.send_message(chat_id=admin_id, text=text)
        except Exception as ex:
            logger.error("Не удалось уведомить админа %s: %s", admin_id, ex)


def request_operator(update: Update, context: CallbackContext) -> None:
    user = update.effective_user
    if user.id in ACTIVE_CHATS:
        update.message.reply_text("Вы уже в чате с оператором. Напишите сообщение.")
        return
    notify_admins(context, user)
    update.message.reply_text("✅ Запрос отправлен оператору. Пожалуйста, подождите.")


def start(update: Update, context: CallbackContext) -> None:
    scenario = load_scenario()
    logger.info("/start user=%s bot=%s", update.effective_user.id, scenario.get("botName"))
    send_step(update, scenario["startStepId"])


def help_command(update: Update, context: CallbackContext) -> None:
    update.message.reply_text("Используйте /start для открытия меню бота.")


def take_command(update: Update, context: CallbackContext) -> None:
    admin_id = update.effective_user.id
    if admin_id not in ADMIN_IDS:
        return
    text = update.message.text.strip()
    try:
        user_id = int(text.split("_", 1)[1])
    except Exception:
        update.message.reply_text("Используйте команду вида /take_123456789")
        return
    if user_id not in WAITING_OPERATOR:
        update.message.reply_text("Такого пользователя нет в очереди.")
        return
    ACTIVE_CHATS[user_id] = admin_id
    ADMIN_ACTIVE_USER[admin_id] = user_id
    WAITING_OPERATOR.pop(user_id, None)
    update.message.reply_text(f"✅ Чат с пользователем {user_id} активирован. Пишите сообщения — они будут отправлены пользователю.")
    context.bot.send_message(chat_id=user_id, text="✅ Оператор подключился. Напишите ваш вопрос.")


def close_command(update: Update, context: CallbackContext) -> None:
    admin_id = update.effective_user.id
    user_id = ADMIN_ACTIVE_USER.pop(admin_id, None)
    if not user_id:
        update.message.reply_text("У вас нет активного чата.")
        return
    ACTIVE_CHATS.pop(user_id, None)
    update.message.reply_text("Чат закрыт.")
    try:
        context.bot.send_message(chat_id=user_id, text="Диалог с оператором завершён.")
    except Exception:
        pass


def handle_text(update: Update, context: CallbackContext) -> None:
    user_id = update.effective_user.id
    text = update.message.text.strip()

    if user_id in ADMIN_IDS and user_id in ADMIN_ACTIVE_USER:
        target_user = ADMIN_ACTIVE_USER[user_id]
        context.bot.send_message(chat_id=target_user, text=f"💬 Ответ оператора:\n\n{text}")
        update.message.reply_text("✅ Отправлено пользователю.")
        return

    if user_id in ACTIVE_CHATS:
        admin_id = ACTIVE_CHATS[user_id]
        context.bot.send_message(chat_id=admin_id, text=f"💬 Сообщение от пользователя {user_id}:\n\n{text}")
        update.message.reply_text("✅ Сообщение отправлено оператору.")
        return

    scenario = load_scenario()
    current_id = USER_STEPS.get(user_id, scenario["startStepId"])
    current_step = get_step(scenario, current_id) or get_step(scenario, scenario["startStepId"])
    if not current_step:
        start(update, context)
        return

    for button in current_step.get("buttons", []):
        if text != button.get("text"):
            continue
        action = str(button.get("action", "message")).lower()
        if action == "go":
            send_step(update, str(button.get("target", scenario["startStepId"])))
            return
        if action == "operator":
            request_operator(update, context)
            return
        if action == "start":
            send_step(update, scenario["startStepId"])
            return
        if action == "end":
            USER_STEPS.pop(user_id, None)
            update.message.reply_text(str(button.get("message", "Диалог завершён.")), reply_markup=ReplyKeyboardRemove())
            return
        update.message.reply_text(str(button.get("message", "Действие выполнено.")), reply_markup=make_keyboard(current_step))
        return

    update.message.reply_text(scenario.get("unknownMessage", "Выберите действие из меню."), reply_markup=make_keyboard(current_step))


def main() -> None:
    if not BOT_TOKEN:
        logger.error("BOT_TOKEN не задан. Укажите токен в .env рядом с bot.py")
        return

    request_kwargs = {"connect_timeout": 20, "read_timeout": 20}
    if PROXY_URL:
        request_kwargs["proxy_url"] = PROXY_URL
        logger.info("Запуск через прокси: %s", PROXY_URL)

    updater = Updater(token=BOT_TOKEN, use_context=True, request_kwargs=request_kwargs)
    dispatcher = updater.dispatcher
    dispatcher.add_handler(CommandHandler("start", start))
    dispatcher.add_handler(CommandHandler("help", help_command))
    dispatcher.add_handler(CommandHandler("close", close_command))
    dispatcher.add_handler(MessageHandler(Filters.regex(r"^/take_\d+$"), take_command))
    dispatcher.add_handler(MessageHandler(Filters.text & ~Filters.command, handle_text))

    logger.info("Бот запущен из рабочей папки: %s", BASE_DIR)
    updater.start_polling()
    updater.idle()


if __name__ == "__main__":
    main()
