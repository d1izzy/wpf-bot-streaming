import os
import re
import sys
import json
import logging
from pathlib import Path
from typing import Any, Dict, List, Optional
from datetime import datetime

# Проверка версии Python
if sys.version_info < (3, 10) or sys.version_info >= (3, 13):
    print("⚠️ ВНИМАНИЕ: Этот бот требует Python 3.10, 3.11 или 3.12")
    print(f"   Текущая версия: Python {sys.version_info.major}.{sys.version_info.minor}.{sys.version_info.micro}")
    print("   Для Python 3.13+ модуль imghdr был удален, используйте Python 3.10-3.12")
    sys.exit(1)

from dotenv import load_dotenv
from telegram import Update, InlineKeyboardButton, InlineKeyboardMarkup, ReplyKeyboardMarkup, KeyboardButton
from telegram.ext import Updater, CommandHandler, MessageHandler, Filters, CallbackQueryHandler, ConversationHandler
from telegram.ext import CallbackContext

# Загрузка переменных окружения
load_dotenv()

# Настройка логирования
logging.basicConfig(
    format='%(asctime)s - %(name)s - %(levelname)s - %(message)s',
    level=logging.INFO
)
logger = logging.getLogger(__name__)

# Константы
BOT_TOKEN = os.getenv('BOT_TOKEN', '').strip()
PROXY_URL = os.getenv('BOTSUPP_PROXY_URL', '').strip() or os.getenv('ALL_PROXY', '').strip()
# Поддержка одного или нескольких админов через запятую в .env: ADMIN_ID=123,456,789
_raw_admin_ids = os.getenv('ADMIN_ID', '1013377447')
try:
    _admin_id_values = [x.strip() for x in _raw_admin_ids.split(',') if x.strip()]
    ADMIN_IDS = frozenset(int(x) for x in _admin_id_values)
except ValueError:
    print("Ошибка: ADMIN_ID должен содержать только числовые Telegram ID через запятую.")
    print(f"Текущее значение ADMIN_ID: '{_raw_admin_ids}'")
    sys.exit(1)

if not ADMIN_IDS:
    print("Ошибка: ADMIN_ID пустой. Укажите хотя бы один Telegram ID в .env.")
    sys.exit(1)

# Состояния для ConversationHandler
WAITING_FOR_OPERATOR, OPERATOR_CHAT = range(2)
CANCEL_OPERATOR_REQUEST_TEXT = "❌ Отменить вызов оператора"

# ============================================================================
# СТРУКТУРА ДАННЫХ
# ============================================================================

# Хранилище запросов пользователей
# Формат: {
#   user_id: {
#       'user_id': int,
#       'username': str,
#       'first_name': str,
#       'last_name': str,
#       'status': 'pending' | 'active' | 'closed',
#       'operator_id': int | None,
#       'created_at': datetime,
#       'activated_at': datetime | None,
#       'closed_at': datetime | None
#   }
# }
user_requests: Dict[int, Dict] = {}

# Текущий активный чат оператора
# Формат: {operator_id: user_id}
operator_active_chat: Dict[int, int] = {}
user_faq_state: Dict[int, Dict[str, str]] = {}

# База знаний FAQ (fallback по умолчанию)
DEFAULT_FAQ_DATABASE = {
    'balance': {
        'question': 'Как узнать баланс счета?',
        'answer': 'Вы можете узнать баланс счета несколькими способами:\n'
                 '1. Через мобильное приложение банка\n'
                 '2. Через банкомат\n'
                 '3. Позвонив на горячую линию\n'
                 '4. В личном кабинете на сайте банка'
    },
    'card_block': {
        'question': 'Как заблокировать карту?',
        'answer': 'Для блокировки карты:\n'
                 '1. Позвоните на горячую линию 24/7\n'
                 '2. Используйте мобильное приложение (раздел "Безопасность")\n'
                 '3. Обратитесь в отделение банка\n\n'
                 '⚠️ При утере карты блокируйте немедленно!'
    },
    'transfer': {
        'question': 'Как перевести деньги?',
        'answer': 'Переводы можно совершить:\n'
                 '1. В мобильном приложении (раздел "Переводы")\n'
                 '2. Через банкомат\n'
                 '3. В личном кабинете на сайте\n'
                 '4. По номеру телефона или карты получателя'
    },
    'loan': {
        'question': 'Информация о кредитах',
        'answer': 'У нас доступны различные кредитные продукты:\n'
                 '• Потребительский кредит\n'
                 '• Кредитная карта\n'
                 '• Ипотека\n'
                 '• Автокредит\n\n'
                 'Для получения подробной информации обратитесь к менеджеру.'
    },
    'deposit': {
        'question': 'Информация о вкладах',
        'answer': 'Мы предлагаем различные виды вкладов:\n'
                 '• Срочные вклады с фиксированной ставкой\n'
                 '• Накопительные счета\n'
                 '• Вклады с возможностью пополнения\n\n'
                 'Актуальные ставки уточняйте у менеджера.'
    },
    'other': {
        'question': 'Другой вопрос',
        'answer': 'Если ваш вопрос не относится к перечисленным категориям, '
                 'вы можете связаться с оператором поддержки.'
    }
}

FAQ_FILE_PATH = Path(__file__).resolve().parent / "faq.json"


ALLOWED_FLOW_ACTIONS = {"operator", "to_menu", "end"}


def _normalize_flow(flow_value: Any, faq_key: str) -> Optional[Dict[str, Any]]:
    if not isinstance(flow_value, dict):
        return None

    start_step_id = str(flow_value.get("startStepId", "")).strip()
    raw_steps = flow_value.get("steps", [])
    if not isinstance(raw_steps, list):
        logger.warning("Flow '%s' пропущен: steps должен быть массивом.", faq_key)
        return None

    steps: List[Dict[str, Any]] = []
    step_ids = set()
    for raw_step in raw_steps:
        if not isinstance(raw_step, dict):
            continue
        step_id = str(raw_step.get("id", "")).strip()
        text = str(raw_step.get("text", "")).strip()
        transitions_raw = raw_step.get("transitions", [])
        if not step_id or not text or not isinstance(transitions_raw, list):
            continue

        transitions: List[Dict[str, str]] = []
        for raw_transition in transitions_raw:
            if not isinstance(raw_transition, dict):
                continue
            caption = str(raw_transition.get("caption", "")).strip()
            target_step_id = str(raw_transition.get("targetStepId", "")).strip()
            action = str(raw_transition.get("action", "")).strip().lower()
            if not caption:
                continue
            if bool(target_step_id) == bool(action):
                continue
            if action and action not in ALLOWED_FLOW_ACTIONS:
                continue
            transitions.append(
                {"caption": caption, "targetStepId": target_step_id, "action": action}
            )

        # Разрешаем шаги без пользовательских переходов:
        # в таком случае остаются дефолтные кнопки "оператор" и "в меню FAQ".
        steps.append({"id": step_id, "text": text, "transitions": transitions})
        step_ids.add(step_id)

    if not steps:
        return None

    if start_step_id not in step_ids:
        start_step_id = steps[0]["id"]
        logger.warning("Flow '%s': startStepId невалиден, используется '%s'.", faq_key, start_step_id)

    for step in steps:
        for transition in step["transitions"]:
            target = transition.get("targetStepId", "")
            if target and target not in step_ids:
                logger.warning("Flow '%s': переход '%s' -> '%s' удален (step не найден).", faq_key, step["id"], target)
                transition["targetStepId"] = ""
                transition["action"] = "to_menu"

    return {"startStepId": start_step_id, "steps": steps}


def _as_bool(value: Any, default: bool = True) -> bool:
    if value is None:
        return default
    if isinstance(value, bool):
        return value
    text = str(value).strip().lower()
    if text in {"1", "true", "yes", "on"}:
        return True
    if text in {"0", "false", "no", "off"}:
        return False
    return default


def load_faq_database() -> Dict[str, Dict[str, Any]]:
    """Загружает FAQ из JSON-файла с fallback на встроенный словарь."""
    if not FAQ_FILE_PATH.exists():
        logger.warning("Файл faq.json не найден, используется fallback FAQ.")
        return DEFAULT_FAQ_DATABASE.copy()

    try:
        with FAQ_FILE_PATH.open("r", encoding="utf-8") as f:
            data = json.load(f)
    except Exception as e:
        logger.error(f"Ошибка чтения faq.json: {e}. Используется fallback FAQ.")
        return DEFAULT_FAQ_DATABASE.copy()

    if not isinstance(data, dict):
        logger.error("Некорректный формат faq.json: корневой объект должен быть dict.")
        return DEFAULT_FAQ_DATABASE.copy()

    normalized: Dict[str, Dict[str, Any]] = {}
    for key, value in data.items():
        if not isinstance(value, dict):
            logger.warning(f"FAQ-элемент '{key}' пропущен: должен быть объектом.")
            continue
        question = str(value.get("question", "")).strip()
        answer = str(value.get("answer", "")).strip()
        if not question or not answer:
            logger.warning(f"FAQ-элемент '{key}' пропущен: пустой question/answer.")
            continue
        normalized_item: Dict[str, Any] = {"question": question, "answer": answer}
        flow_enabled = _as_bool(value.get("flowEnabled"), True)
        flow = _normalize_flow(value.get("flow"), str(key))
        if flow and flow_enabled:
            normalized_item["flow"] = flow
        normalized[str(key)] = normalized_item

    if not normalized:
        logger.error("После валидации faq.json пуст. Используется fallback FAQ.")
        return DEFAULT_FAQ_DATABASE.copy()

    return normalized


FAQ_DATABASE = load_faq_database()


def refresh_faq_database() -> None:
    """Обновляет FAQ из файла faq.json в рантайме."""
    global FAQ_DATABASE
    FAQ_DATABASE = load_faq_database()


# ============================================================================
# ВСПОМОГАТЕЛЬНЫЕ ФУНКЦИИ
# ============================================================================

def get_faq_keyboard():
    """Создает клавиатуру с FAQ пунктами"""
    refresh_faq_database()
    keyboard = []
    for key, data in FAQ_DATABASE.items():
        keyboard.append([KeyboardButton(data['question'])])
    keyboard.append([KeyboardButton('💬 Связаться с оператором')])
    return ReplyKeyboardMarkup(keyboard, resize_keyboard=True, one_time_keyboard=False)


def get_waiting_operator_keyboard():
    """Клавиатура для пользователя в очереди к оператору."""
    refresh_faq_database()
    keyboard = []
    for _, data in FAQ_DATABASE.items():
        keyboard.append([KeyboardButton(data['question'])])
    keyboard.append([KeyboardButton('💬 Связаться с оператором')])
    keyboard.append([KeyboardButton(CANCEL_OPERATOR_REQUEST_TEXT)])
    return ReplyKeyboardMarkup(keyboard, resize_keyboard=True, one_time_keyboard=False)


def get_flow_step_keyboard(step: Dict[str, Any]) -> ReplyKeyboardMarkup:
    keyboard = [[KeyboardButton(t["caption"])] for t in step.get("transitions", [])]
    keyboard.append([KeyboardButton("💬 Связаться с оператором")])
    keyboard.append([KeyboardButton("↩️ В меню FAQ")])
    return ReplyKeyboardMarkup(keyboard, resize_keyboard=True, one_time_keyboard=False)


def get_flow_step_by_id(flow: Dict[str, Any], step_id: str) -> Optional[Dict[str, Any]]:
    for step in flow.get("steps", []):
        if step.get("id") == step_id:
            return step
    return None


def start_flow_for_user(update: Update, user_id: int, faq_key: str, faq_data: Dict[str, Any]) -> bool:
    flow = faq_data.get("flow")
    if not isinstance(flow, dict):
        return False
    step_id = str(flow.get("startStepId", "")).strip()
    step = get_flow_step_by_id(flow, step_id)
    if step is None:
        return False

    user_faq_state[user_id] = {"faq_key": faq_key, "step_id": step_id}
    logger.info("Пользователь %s начал FAQ-сценарий '%s' с шага '%s'.", user_id, faq_key, step_id)
    update.message.reply_text(step.get("text", ""), reply_markup=get_flow_step_keyboard(step))
    return True


def process_flow_transition(update: Update, context: CallbackContext, user_id: int, message_text: str) -> bool:
    state = user_faq_state.get(user_id)
    if not state:
        return False

    faq_data = FAQ_DATABASE.get(state.get("faq_key", ""))
    if not faq_data or "flow" not in faq_data:
        user_faq_state.pop(user_id, None)
        return False

    flow = faq_data["flow"]
    step = get_flow_step_by_id(flow, state.get("step_id", ""))
    if step is None:
        user_faq_state.pop(user_id, None)
        return False

    if message_text == "↩️ В меню FAQ":
        user_faq_state.pop(user_id, None)
        update.message.reply_text("Вы вернулись в главное меню FAQ.", reply_markup=get_faq_keyboard())
        return True

    if message_text == "💬 Связаться с оператором":
        user_faq_state.pop(user_id, None)
        request_operator(update, context)
        return True

    for transition in step.get("transitions", []):
        if message_text != transition.get("caption"):
            continue

        action = transition.get("action", "")
        target_step_id = transition.get("targetStepId", "")

        if target_step_id:
            next_step = get_flow_step_by_id(flow, target_step_id)
            if next_step is None:
                user_faq_state.pop(user_id, None)
                update.message.reply_text("Сценарий временно недоступен. Возвращаю в меню FAQ.", reply_markup=get_faq_keyboard())
                return True
            user_faq_state[user_id]["step_id"] = target_step_id
            logger.info("Пользователь %s перешел в FAQ-сценарии на шаг '%s'.", user_id, target_step_id)
            update.message.reply_text(next_step.get("text", ""), reply_markup=get_flow_step_keyboard(next_step))
            return True

        if action == "operator":
            user_faq_state.pop(user_id, None)
            logger.info("Пользователь %s выбрал эскалацию к оператору из FAQ-сценария.", user_id)
            request_operator(update, context)
            return True

        user_faq_state.pop(user_id, None)
        update.message.reply_text("Вы в меню FAQ.", reply_markup=get_faq_keyboard())
        return True

    update.message.reply_text("Выберите один из предложенных вариантов.", reply_markup=get_flow_step_keyboard(step))
    return True


def get_pending_users_keyboard():
    """Создает клавиатуру со списком ожидающих пользователей"""
    keyboard = []
    
    for user_id, request_info in user_requests.items():
        if request_info['status'] == 'pending':
            username = request_info.get('username', 'не указан')
            first_name = request_info.get('first_name', 'Пользователь')
            # Ограничиваем длину текста кнопки
            button_text = f"👤 {first_name} (@{username})"[:40]
            keyboard.append([
                InlineKeyboardButton(button_text, callback_data=f"select_{user_id}")
            ])
    
    if not keyboard:
        return None
    
    return InlineKeyboardMarkup(keyboard)


def get_admin_main_keyboard():
    """Создает главное меню для админа"""
    keyboard = []
    
    # Подсчитываем статистику
    pending_count = len([r for r in user_requests.values() if r['status'] == 'pending'])
    active_count = len([r for r in user_requests.values() if r['status'] == 'active'])
    
    # Кнопка обновить список ожидающих
    keyboard.append([
        InlineKeyboardButton(
            f"🔄 Обновить список ({pending_count} в очереди)",
            callback_data="admin_refresh"
        )
    ])
    
    # Кнопка показать активные чаты
    if active_count > 0:
        keyboard.append([
            InlineKeyboardButton(
                f"💬 Активные чаты ({active_count})",
                callback_data="admin_active_chats"
            )
        ])
    
    # Кнопка статистика
    keyboard.append([
        InlineKeyboardButton("📊 Статистика", callback_data="admin_stats")
    ])
    
    return InlineKeyboardMarkup(keyboard)


def get_exit_chat_keyboard(user_id: int):
    """Создает клавиатуру с кнопкой выхода из чата для оператора"""
    return InlineKeyboardMarkup([[
        InlineKeyboardButton("❌ Выйти из чата", callback_data=f"exit_{user_id}")
    ]])

def get_user_exit_chat_keyboard():
    """Создает клавиатуру с кнопкой выхода из чата для пользователя"""
    return InlineKeyboardMarkup([[
        InlineKeyboardButton("❌ Завершить диалог с оператором", callback_data="user_exit_chat")
    ]])


def send_pending_users_list_to_operator(context: CallbackContext, operator_id: int):
    """Отправляет или обновляет список ожидающих пользователей оператору"""
    keyboard = get_pending_users_keyboard()
    
    if keyboard:
        pending_count = len([r for r in user_requests.values() if r['status'] == 'pending'])
        message = (
            f"📋 <b>Ожидающие пользователи: {pending_count}</b>\n\n"
            f"Выберите пользователя из списка для начала диалога:"
        )
        try:
            context.bot.send_message(
                chat_id=operator_id,
                text=message,
                reply_markup=keyboard,
                parse_mode='HTML'
            )
        except Exception as e:
            logger.error(f"Ошибка при отправке списка пользователей оператору: {e}")
    else:
        # Нет ожидающих пользователей
        try:
            context.bot.send_message(
                chat_id=operator_id,
                text="ℹ️ Нет ожидающих пользователей.",
                parse_mode='HTML'
            )
        except Exception as e:
            logger.error(f"Ошибка при отправке сообщения оператору: {e}")


def activate_chat_with_user(context: CallbackContext, operator_id: int, user_id: int) -> bool:
    """
    Активирует чат между оператором и пользователем.
    Возвращает True если успешно, False если ошибка.
    """
    if user_id not in user_requests:
        logger.warning(f"Попытка активировать чат с несуществующим пользователем {user_id}")
        return False
    
    request_info = user_requests[user_id]
    
    # Проверяем, что запрос еще в статусе pending
    if request_info['status'] != 'pending':
        logger.warning(f"Попытка активировать чат с пользователем {user_id}, статус: {request_info['status']}")
        return False
    
    # Обновляем статус запроса
    request_info['status'] = 'active'
    request_info['operator_id'] = operator_id
    request_info['activated_at'] = datetime.now()
    
    # Устанавливаем активный чат для оператора
    operator_active_chat[operator_id] = user_id
    
    # Уведомляем оператора
    keyboard = get_exit_chat_keyboard(user_id)
    operator_message = (
        f"✅ <b>Чат с пользователем активирован</b>\n\n"
        f"👤 Имя: {request_info.get('first_name', 'Не указано')}\n"
        f"🆔 ID: <code>{user_id}</code>\n"
        f"📱 Username: @{request_info.get('username', 'не указан')}\n\n"
        f"💬 Теперь вы можете писать сообщения напрямую - они будут отправлены пользователю.\n\n"
        f"Для выхода из чата нажмите кнопку ниже."
    )
    
    try:
        context.bot.send_message(
            chat_id=operator_id,
            text=operator_message,
            reply_markup=keyboard,
            parse_mode='HTML'
        )
    except Exception as e:
        logger.error(f"Ошибка при уведомлении оператора: {e}")
        return False
    
    # Уведомляем пользователя
    try:
        keyboard = get_user_exit_chat_keyboard()
        context.bot.send_message(
            chat_id=user_id,
            text="✅ Оператор принял ваш запрос! Теперь вы можете задать свой вопрос.\n\n"
                 "Используйте кнопку ниже, если хотите завершить диалог.",
            reply_markup=keyboard
        )
    except Exception as e:
        logger.error(f"Ошибка при уведомлении пользователя {user_id}: {e}")
        # Пользователь мог заблокировать бота, но чат все равно активирован
    
    return True


def close_chat_with_user(context: CallbackContext, operator_id: int, user_id: int) -> bool:
    """
    Закрывает чат между оператором и пользователем.
    Возвращает True если успешно, False если ошибка.
    """
    if user_id not in user_requests:
        logger.warning(f"Попытка закрыть чат с несуществующим пользователем {user_id}")
        return False
    
    request_info = user_requests[user_id]
    
    # Проверяем, что чат активен и принадлежит этому оператору
    if request_info['status'] != 'active' or request_info['operator_id'] != operator_id:
        logger.warning(f"Попытка закрыть неактивный чат с пользователем {user_id}")
        return False
    
    # Обновляем статус
    request_info['status'] = 'closed'
    request_info['closed_at'] = datetime.now()
    
    # Удаляем активный чат оператора, если это текущий чат
    if operator_id in operator_active_chat and operator_active_chat[operator_id] == user_id:
        del operator_active_chat[operator_id]
    
    # Уведомляем пользователя
    try:
        context.bot.send_message(
            chat_id=user_id,
            text="✅ Диалог с оператором завершен. Если у вас есть еще вопросы, "
                 "вы можете использовать меню FAQ или снова связаться с оператором.",
            reply_markup=get_faq_keyboard()
        )
    except Exception as e:
        logger.error(f"Ошибка при уведомлении пользователя {user_id} о закрытии чата: {e}")
        # Пользователь мог заблокировать бота
    
    # Удаляем запрос из списка (или оставляем для истории - на ваше усмотрение)
    # Для простоты удаляем, но можно оставить для статистики
    del user_requests[user_id]
    
    return True


# ============================================================================
# ОБРАБОТЧИКИ ДЛЯ ПОЛЬЗОВАТЕЛЕЙ
# ============================================================================

def start(update: Update, context: CallbackContext) -> None:
    """Обработчик команды /start"""
    user = update.effective_user
    user_id = user.id
    user_faq_state.pop(user_id, None)
    
    # Если это админ - показываем главное меню
    if user_id in ADMIN_IDS:
        # Подсчитываем статистику
        pending_count = len([r for r in user_requests.values() if r['status'] == 'pending'])
        active_count = len([r for r in user_requests.values() if r['status'] == 'active'])
        
        # Приветствие для админа
        welcome_text = (
            f"👋 <b>Добро пожаловать, оператор!</b>\n\n"
            f"📊 <b>Статистика:</b>\n"
            f"⏳ В очереди: <b>{pending_count}</b>\n"
            f"💬 Активных чатов: <b>{active_count}</b>\n\n"
        )
        
        # Если есть активный чат у этого оператора, показываем информацию о нем
        if user_id in operator_active_chat:
            active_user_id = operator_active_chat[user_id]
            if active_user_id in user_requests and user_requests[active_user_id]['status'] == 'active':
                request_info = user_requests[active_user_id]
                welcome_text += (
                    f"💬 <b>Текущий активный чат:</b>\n"
                    f"👤 {request_info.get('first_name', 'Не указано')}\n"
                    f"🆔 ID: <code>{active_user_id}</code>\n"
                    f"📱 Username: @{request_info.get('username', 'не указан')}\n\n"
                    f"Пишите сообщения напрямую - они будут отправлены пользователю.\n\n"
                )
        
        # Показываем список ожидающих, если есть
        if pending_count > 0:
            welcome_text += f"📋 <b>Ожидающие пользователи ({pending_count}):</b>\n\n"
            pending_list = []
            for user_id_req, request_info in user_requests.items():
                if request_info['status'] == 'pending':
                    username = request_info.get('username', 'не указан')
                    first_name = request_info.get('first_name', 'Пользователь')
                    pending_list.append(f"👤 {first_name} (@{username})")
            
            if pending_list:
                welcome_text += "\n".join(pending_list[:10])  # Показываем максимум 10
                if pending_count > 10:
                    welcome_text += f"\n... и еще {pending_count - 10}"
        
        keyboard = get_admin_main_keyboard()
        update.message.reply_text(
            welcome_text,
            reply_markup=keyboard,
            parse_mode='HTML'
        )
        
        # Если есть активный чат у этого оператора, добавляем кнопку выхода
        if user_id in operator_active_chat:
            active_user_id = operator_active_chat[user_id]
            if active_user_id in user_requests and user_requests[active_user_id]['status'] == 'active':
                exit_keyboard = get_exit_chat_keyboard(active_user_id)
                update.message.reply_text(
                    "Используйте кнопку ниже для выхода из текущего чата:",
                    reply_markup=exit_keyboard
                )
        
        return
    
    # Для обычных пользователей
    # Если пользователь уже в активном чате, показываем информацию с кнопкой выхода
    if user_id in user_requests and user_requests[user_id]['status'] == 'active':
        keyboard = get_user_exit_chat_keyboard()
        update.message.reply_text(
            "💬 Вы уже в чате с оператором. Отправьте ваше сообщение.\n\n"
            "Используйте кнопку ниже, если хотите завершить диалог.",
            reply_markup=keyboard
        )
        return
    
    welcome_message = (
        f"👋 Здравствуйте, {user.first_name}!\n\n"
        "Я бот поддержки. Выберите интересующий вас вопрос из меню ниже, "
        "или свяжитесь с оператором для решения сложных вопросов."
    )
    
    update.message.reply_text(
        welcome_message,
        reply_markup=get_faq_keyboard()
    )


def help_command(update: Update, context: CallbackContext) -> None:
    """Обработчик команды /help для пользователей"""
    help_text = (
        "ℹ️ <b>Справка по использованию бота:</b>\n\n"
        "• Выберите интересующий вопрос из меню FAQ\n"
        "• Если ответ не помог, нажмите кнопку '💬 Связаться с оператором'\n"
        "• Оператор ответит вам в ближайшее время\n\n"
        "Для начала работы используйте /start"
    )
    update.message.reply_text(help_text, parse_mode='HTML', reply_markup=get_faq_keyboard())


def handle_faq(update: Update, context: CallbackContext) -> None:
    """Обработчик выбора FAQ пункта"""
    refresh_faq_database()
    user_message = update.message.text
    
    # Поиск соответствующего FAQ
    for key, data in FAQ_DATABASE.items():
        if user_message == data['question']:
            answer = f"❓ {data['question']}\n\n{data['answer']}"
            if "flow" in data:
                update.message.reply_text(answer)
                if start_flow_for_user(update, update.effective_user.id, key, data):
                    return
            
            # Просто показываем ответ, БЕЗ отправки админу
            # Кнопка для связи с оператором, если ответ не помог
            keyboard = InlineKeyboardMarkup([[
                InlineKeyboardButton("💬 Нужна помощь оператора", callback_data=f"operator_{update.effective_user.id}")
            ]])
            
            update.message.reply_text(
                answer,
                reply_markup=keyboard
            )
            return
    
    # Если сообщение не распознано как FAQ
    if user_message == '💬 Связаться с оператором':
        request_operator(update, context)
    else:
        # Предлагаем связаться с оператором
        keyboard = InlineKeyboardMarkup([[
            InlineKeyboardButton("💬 Связаться с оператором", callback_data=f"operator_{update.effective_user.id}")
        ]])
        update.message.reply_text(
            "Я не нашел ответ на ваш вопрос в базе знаний.\n"
            "Хотите связаться с оператором?",
            reply_markup=keyboard
        )


def request_operator(update: Update, context: CallbackContext) -> int:
    """Запрос связи с оператором"""
    user = update.effective_user
    user_id = user.id
    user_faq_state.pop(user_id, None)
    logger.info("Пользователь %s запросил связь с оператором.", user_id)
    
    # Определяем, откуда пришел запрос - из сообщения или callback
    if update.message:
        reply_func = update.message.reply_text
    elif update.callback_query:
        reply_func = lambda text, **kwargs: context.bot.send_message(
            chat_id=user_id, text=text, **kwargs
        )
    else:
        reply_func = lambda text, **kwargs: context.bot.send_message(
            chat_id=user_id, text=text, **kwargs
        )
    
    # Сценарий 1: Пользователь уже в активном чате
    if user_id in user_requests and user_requests[user_id]['status'] == 'active':
        logger.info("Пользователь %s уже в активном чате с оператором.", user_id)
        reply_func(
            "Вы уже в чате с оператором. Отправьте ваше сообщение, и оператор ответит.",
            reply_markup=get_faq_keyboard()
        )
        return OPERATOR_CHAT
    
    # Сценарий 2: Пользователь уже в очереди (pending)
    if user_id in user_requests and user_requests[user_id]['status'] == 'pending':
        logger.info("Пользователь %s уже находится в очереди к оператору.", user_id)
        reply_func(
            "⏳ Ваш запрос уже в очереди. Пожалуйста, подождите, оператор скоро с вами свяжется.",
            reply_markup=get_waiting_operator_keyboard()
        )
        return WAITING_FOR_OPERATOR
    
    # Сценарий 3: Новый запрос
    try:
        # Создаем новый запрос
        user_requests[user_id] = {
            'user_id': user_id,
            'username': user.username or 'не указан',
            'first_name': user.first_name or 'Не указано',
            'last_name': user.last_name or '',
            'status': 'pending',
            'operator_id': None,
            'created_at': datetime.now(),
            'activated_at': None,
            'closed_at': None
        }
        
        # Отправляем список ожидающих пользователей всем операторам (админам)
        for admin_id in ADMIN_IDS:
            send_pending_users_list_to_operator(context, admin_id)

        logger.info(
            "Создан новый запрос к оператору: user_id=%s username=%s",
            user_id,
            user.username or "не указан"
        )
        
        reply_func(
            "✅ Ваш запрос отправлен оператору. Пожалуйста, подождите, "
            "оператор скоро с вами свяжется.\n\n"
            "Если передумаете, нажмите «❌ Отменить вызов оператора».",
            reply_markup=get_waiting_operator_keyboard()
        )
        
        return WAITING_FOR_OPERATOR
        
    except Exception as e:
        logger.error(f"Ошибка при создании запроса оператору: {e}")
        reply_func(
            "❌ Произошла ошибка при отправке запроса. Попробуйте позже.",
            reply_markup=get_faq_keyboard()
        )
        return ConversationHandler.END


def handle_user_message(update: Update, context: CallbackContext) -> None:
    """Обработчик сообщений от пользователя"""
    user_id = update.effective_user.id
    message_text = update.message.text
    if process_flow_transition(update, context, user_id, message_text):
        return
    
    # Сценарий 1: Пользователь в активном чате
    if user_id in user_requests and user_requests[user_id]['status'] == 'active':
        request_info = user_requests[user_id]
        operator_id = request_info['operator_id']
        
        if operator_id is None:
            # Странная ситуация - статус active, но нет оператора
            logger.error(f"Пользователь {user_id} в статусе active, но operator_id = None")
            user_requests[user_id]['status'] = 'pending'
            update.message.reply_text(
                "⏳ Ваш запрос обрабатывается. Пожалуйста, подождите.",
                reply_markup=get_faq_keyboard()
            )
            return
        
        # Отправляем сообщение оператору
        operator_message = (
            f"💬 <b>Сообщение от пользователя</b>\n\n"
            f"👤 {update.effective_user.first_name} {update.effective_user.last_name or ''}\n"
            f"🆔 ID: <code>{user_id}</code>\n"
            f"📱 Username: @{update.effective_user.username or 'не указан'}\n\n"
            f"📝 {message_text}"
        )
        
        # Если это текущий активный чат оператора, добавляем кнопку выхода
        keyboard = None
        if operator_id in operator_active_chat and operator_active_chat[operator_id] == user_id:
            keyboard = get_exit_chat_keyboard(user_id)
        
        try:
            context.bot.send_message(
                chat_id=operator_id,
                text=operator_message,
                parse_mode='HTML',
                reply_markup=keyboard
            )
            
            # Отправляем подтверждение пользователю с кнопкой выхода
            user_keyboard = get_user_exit_chat_keyboard()
            update.message.reply_text(
                "✅ Ваше сообщение отправлено оператору.",
                reply_markup=user_keyboard
            )
        except Exception as e:
            logger.error(f"Ошибка при отправке сообщения оператору {operator_id}: {e}")
            # Возможно оператор заблокировал бота или произошла ошибка
            update.message.reply_text(
                "❌ Ошибка при отправке сообщения. Попробуйте позже или свяжитесь с оператором снова.",
                reply_markup=get_faq_keyboard()
            )
            # Сбрасываем статус в pending
            user_requests[user_id]['status'] = 'pending'
            user_requests[user_id]['operator_id'] = None
    
    # Сценарий 2: Пользователь в очереди (pending)
    elif user_id in user_requests and user_requests[user_id]['status'] == 'pending':
        if message_text == CANCEL_OPERATOR_REQUEST_TEXT:
            user_info = user_requests.pop(user_id, None)
            for admin_id in ADMIN_IDS:
                send_pending_users_list_to_operator(context, admin_id)

            update.message.reply_text(
                "✅ Вызов оператора отменен. Вы можете продолжить работу с FAQ.",
                reply_markup=get_faq_keyboard()
            )
            logger.info(
                "Пользователь %s отменил запрос оператору (pending).",
                user_info.get('user_id') if user_info else user_id
            )
            return

        logger.info("Пользователь %s все еще ожидает оператора (pending).", user_id)
        update.message.reply_text(
            "⏳ Ваш запрос еще не принят оператором. Пожалуйста, подождите.\n"
            "Если хотите отменить ожидание, нажмите «❌ Отменить вызов оператора».",
            reply_markup=get_waiting_operator_keyboard()
        )
    
    # Сценарий 3: Обычная обработка FAQ
    else:
        handle_faq(update, context)


# ============================================================================
# ОБРАБОТЧИКИ ДЛЯ ОПЕРАТОРА (АДМИНА)
# ============================================================================

def handle_operator_callback(update: Update, context: CallbackContext) -> None:
    """Обработчик callback от кнопок"""
    query = update.callback_query
    query.answer()
    
    data = query.data
    user_id_from_query = query.from_user.id
    
    # Callback от пользователя: выход из чата
    if data == 'user_exit_chat':
        user_id = user_id_from_query
        
        # Проверяем, что пользователь в активном чате
        if user_id not in user_requests or user_requests[user_id]['status'] != 'active':
            query.answer("Вы не в активном чате с оператором.", show_alert=True)
            return
        
        request_info = user_requests[user_id]
        operator_id = request_info.get('operator_id')
        
        # Уведомляем оператора о закрытии чата пользователем
        if operator_id:
            try:
                context.bot.send_message(
                    chat_id=operator_id,
                    text=(
                        f"⚠️ <b>Пользователь завершил диалог</b>\n\n"
                        f"👤 {request_info.get('first_name', 'Не указано')}\n"
                        f"🆔 ID: <code>{user_id}</code>\n"
                        f"📱 Username: @{request_info.get('username', 'не указан')}\n\n"
                        f"Пользователь самостоятельно завершил диалог."
                    ),
                    parse_mode='HTML'
                )
            except Exception as e:
                logger.error(f"Ошибка при уведомлении оператора о закрытии чата: {e}")
        
        # Закрываем чат
        if operator_id:
            close_chat_with_user(context, operator_id, user_id)
        else:
            # Если нет оператора, просто удаляем запрос
            del user_requests[user_id]
        
        query.answer("Диалог завершен!")
        context.bot.send_message(
            chat_id=user_id,
            text="✅ Вы завершили диалог с оператором. Если у вас есть еще вопросы, "
                 "вы можете использовать меню FAQ или снова связаться с оператором.",
            reply_markup=get_faq_keyboard()
        )
        return
    
    # Callback от пользователя: "Связаться с оператором"
    if data.startswith('operator_'):
        user_id = int(data.split('_')[1])
        # Проверяем, что это тот же пользователь
        if user_id_from_query != user_id:
            query.answer("Это не ваш запрос.", show_alert=True)
            return
        
        # Обрабатываем запрос напрямую
        user = query.from_user
        
        # Сценарий 1: Пользователь уже в активном чате
        if user_id in user_requests and user_requests[user_id]['status'] == 'active':
            context.bot.send_message(
                chat_id=user_id,
                text="Вы уже в чате с оператором. Отправьте ваше сообщение, и оператор ответит.",
                reply_markup=get_faq_keyboard()
            )
            query.answer("Вы уже в чате с оператором.")
            return
        
        # Сценарий 2: Пользователь уже в очереди (pending)
        if user_id in user_requests and user_requests[user_id]['status'] == 'pending':
            context.bot.send_message(
                chat_id=user_id,
                text="⏳ Ваш запрос уже в очереди. Пожалуйста, подождите, оператор скоро с вами свяжется.",
                reply_markup=get_faq_keyboard()
            )
            query.answer("Ваш запрос уже в очереди.")
            return
        
        # Сценарий 3: Новый запрос
        try:
            # Создаем новый запрос
            user_requests[user_id] = {
                'user_id': user_id,
                'username': user.username or 'не указан',
                'first_name': user.first_name or 'Не указано',
                'last_name': user.last_name or '',
                'status': 'pending',
                'operator_id': None,
                'created_at': datetime.now(),
                'activated_at': None,
                'closed_at': None
            }
            
            # Отправляем список ожидающих пользователей всем операторам (админам)
            for admin_id in ADMIN_IDS:
                send_pending_users_list_to_operator(context, admin_id)
            
            # Отправляем подтверждение пользователю
            context.bot.send_message(
                chat_id=user_id,
                text="✅ Ваш запрос отправлен оператору. Пожалуйста, подождите, "
                     "оператор скоро с вами свяжется.\n\n"
                     "Вы можете продолжить использовать меню FAQ.",
                reply_markup=get_faq_keyboard()
            )
            
            query.answer("Запрос отправлен оператору!")
            
        except Exception as e:
            logger.error(f"Ошибка при создании запроса оператору: {e}")
            context.bot.send_message(
                chat_id=user_id,
                text="❌ Произошла ошибка при отправке запроса. Попробуйте позже.",
                reply_markup=get_faq_keyboard()
            )
            query.answer("Ошибка при отправке запроса.", show_alert=True)
        
        return
    
    # Проверяем, что это админ для остальных callback
    if user_id_from_query not in ADMIN_IDS:
        return
    
    operator_id = user_id_from_query  # Определяем operator_id
    
    # Оператор выбирает пользователя из списка
    if data.startswith('select_'):
        try:
            user_id = int(data.split('_')[1])
            logger.info(f"Оператор {operator_id} выбирает пользователя {user_id}")
            
            # Проверяем, что пользователь еще в очереди
            if user_id not in user_requests:
                query.edit_message_text("❌ Пользователь больше не в очереди.")
                query.answer("Пользователь больше не в очереди.", show_alert=True)
                return
            
            if user_requests[user_id]['status'] != 'pending':
                query.edit_message_text(f"❌ Пользователь уже обработан (статус: {user_requests[user_id]['status']}).")
                query.answer(f"Пользователь уже обработан.", show_alert=True)
                send_pending_users_list_to_operator(context, operator_id)
                return
            
            # Если у оператора уже есть активный чат, закрываем его
            if operator_id in operator_active_chat:
                old_user_id = operator_active_chat[operator_id]
                if old_user_id in user_requests and user_requests[old_user_id]['status'] == 'active':
                    # Закрываем старый чат
                    close_chat_with_user(context, operator_id, old_user_id)
                    context.bot.send_message(
                        chat_id=operator_id,
                        text=f"ℹ️ Предыдущий чат с пользователем {old_user_id} был закрыт."
                    )
            
            # Активируем новый чат
            if activate_chat_with_user(context, operator_id, user_id):
                query.edit_message_text(
                    f"✅ Чат с пользователем {user_id} активирован.",
                    parse_mode='HTML'
                )
                query.answer("Чат активирован!")
                # Обновляем список ожидающих
                send_pending_users_list_to_operator(context, operator_id)
            else:
                query.edit_message_text("❌ Ошибка при активации чата.")
                query.answer("Ошибка при активации чата.", show_alert=True)
        except Exception as e:
            logger.error(f"Ошибка при выборе пользователя: {e}")
            query.answer(f"Ошибка: {str(e)}", show_alert=True)
    
    # Оператор выходит из чата
    if data.startswith('exit_'):
        user_id = int(data.split('_')[1])
        
        if close_chat_with_user(context, operator_id, user_id):
            query.edit_message_text(
                f"✅ Чат с пользователем {user_id} завершен.\n\n"
                f"Используйте /start для просмотра главного меню."
            )
            query.answer("Чат закрыт!")
        else:
            query.edit_message_text("❌ Ошибка при закрытии чата.")
            query.answer("Ошибка при закрытии чата.", show_alert=True)
    
    # Обновить список ожидающих
    elif data == 'admin_refresh':
        pending_count = len([r for r in user_requests.values() if r['status'] == 'pending'])
        keyboard = get_pending_users_keyboard()
        
        if keyboard:
            query.edit_message_text(
                f"📋 <b>Ожидающие пользователи: {pending_count}</b>\n\n"
                f"Выберите пользователя из списка для начала диалога:",
                reply_markup=keyboard,
                parse_mode='HTML'
            )
        else:
            query.edit_message_text(
                "ℹ️ Нет ожидающих пользователей.",
                reply_markup=get_admin_main_keyboard()
            )
        query.answer("Список обновлен!")
    
    # Показать активные чаты
    elif data == 'admin_active_chats':
        active_chats = []
        for user_id, request_info in user_requests.items():
            if request_info['status'] == 'active' and request_info['operator_id'] == operator_id:
                username = request_info.get('username', 'не указан')
                first_name = request_info.get('first_name', 'Пользователь')
                active_chats.append(f"👤 {first_name} (@{username}) - ID: {user_id}")
        
        if active_chats:
            keyboard = InlineKeyboardMarkup([[
                InlineKeyboardButton("🔙 Назад в меню", callback_data="admin_back")
            ]])
            query.edit_message_text(
                f"💬 <b>Активные чаты ({len(active_chats)}):</b>\n\n" + "\n".join(active_chats),
                reply_markup=keyboard,
                parse_mode='HTML'
            )
        else:
            query.edit_message_text(
                "ℹ️ У вас нет активных чатов.",
                reply_markup=get_admin_main_keyboard()
            )
        query.answer()
    
    # Статистика
    elif data == 'admin_stats':
        pending_count = len([r for r in user_requests.values() if r['status'] == 'pending'])
        active_count = len([r for r in user_requests.values() if r['status'] == 'active'])
        closed_count = len([r for r in user_requests.values() if r['status'] == 'closed'])
        total_count = len(user_requests)
        
        stats_text = (
            f"📊 <b>Статистика бота:</b>\n\n"
            f"⏳ В очереди: <b>{pending_count}</b>\n"
            f"💬 Активных чатов: <b>{active_count}</b>\n"
            f"✅ Завершено: <b>{closed_count}</b>\n"
            f"📈 Всего запросов: <b>{total_count}</b>"
        )
        
        keyboard = InlineKeyboardMarkup([[
            InlineKeyboardButton("🔙 Назад в меню", callback_data="admin_back")
        ]])
        query.edit_message_text(
            stats_text,
            reply_markup=keyboard,
            parse_mode='HTML'
        )
        query.answer()
    
    # Назад в главное меню
    elif data == 'admin_back':
        pending_count = len([r for r in user_requests.values() if r['status'] == 'pending'])
        active_count = len([r for r in user_requests.values() if r['status'] == 'active'])
        
        welcome_text = (
            f"👋 <b>Главное меню оператора</b>\n\n"
            f"📊 <b>Статистика:</b>\n"
            f"⏳ В очереди: <b>{pending_count}</b>\n"
            f"💬 Активных чатов: <b>{active_count}</b>\n\n"
        )
        
        if operator_id in operator_active_chat:
            active_user_id = operator_active_chat[operator_id]
            if active_user_id in user_requests and user_requests[active_user_id]['status'] == 'active':
                request_info = user_requests[active_user_id]
                welcome_text += (
                    f"💬 <b>Текущий активный чат:</b>\n"
                    f"👤 {request_info.get('first_name', 'Не указано')}\n"
                    f"🆔 ID: <code>{active_user_id}</code>\n\n"
                )
        
        query.edit_message_text(
            welcome_text,
            reply_markup=get_admin_main_keyboard(),
            parse_mode='HTML'
        )
        query.answer()


def handle_operator_message(update: Update, context: CallbackContext) -> None:
    """Обработчик сообщений от оператора (админа)"""
    operator_id = update.effective_user.id
    
    if operator_id not in ADMIN_IDS:
        return
    
    message_text = update.message.text
    
    # Команда /start или /help - показать список ожидающих и текущий активный чат
    if message_text == '/start' or message_text == '/help':
        # Показываем текущий активный чат, если есть
        if operator_id in operator_active_chat:
            user_id = operator_active_chat[operator_id]
            if user_id in user_requests and user_requests[user_id]['status'] == 'active':
                request_info = user_requests[user_id]
                keyboard = get_exit_chat_keyboard(user_id)
                update.message.reply_text(
                    f"💬 <b>Текущий активный чат:</b>\n\n"
                    f"👤 {request_info.get('first_name', 'Не указано')}\n"
                    f"🆔 ID: <code>{user_id}</code>\n"
                    f"📱 Username: @{request_info.get('username', 'не указан')}\n\n"
                    f"Пишите сообщения напрямую - они будут отправлены пользователю.",
                    reply_markup=keyboard,
                    parse_mode='HTML'
                )
        
        # Показываем список ожидающих
        send_pending_users_list_to_operator(context, operator_id)
        return
    
    # Команда /list - показать список ожидающих
    if message_text == '/list':
        send_pending_users_list_to_operator(context, operator_id)
        return
    
    # Если у оператора есть активный чат, отправляем сообщение пользователю
    if operator_id in operator_active_chat:
        user_id = operator_active_chat[operator_id]
        
        # Проверяем, что чат все еще активен
        if user_id not in user_requests:
            del operator_active_chat[operator_id]
            update.message.reply_text("❌ Чат с пользователем больше не активен.")
            send_pending_users_list_to_operator(context, operator_id)
            return
        
        request_info = user_requests[user_id]
        if request_info['status'] != 'active' or request_info['operator_id'] != operator_id:
            del operator_active_chat[operator_id]
            update.message.reply_text("❌ Чат с пользователем больше не активен.")
            send_pending_users_list_to_operator(context, operator_id)
            return
        
        # Отправляем сообщение пользователю
        try:
            context.bot.send_message(
                chat_id=user_id,
                text=f"💬 <b>Ответ от поддержки:</b>\n\n{message_text}",
                parse_mode='HTML',
                reply_markup=get_faq_keyboard()
            )
            
            # Показываем оператору подтверждение с кнопкой выхода
            keyboard = get_exit_chat_keyboard(user_id)
            update.message.reply_text(
                "✅ Сообщение отправлено пользователю.",
                reply_markup=keyboard
            )
        except Exception as e:
            logger.error(f"Ошибка при отправке сообщения пользователю {user_id}: {e}")
            update.message.reply_text(
                "❌ Ошибка при отправке сообщения. Возможно, пользователь заблокировал бота."
            )
            # Закрываем чат, так как пользователь недоступен
            close_chat_with_user(context, operator_id, user_id)
            send_pending_users_list_to_operator(context, operator_id)
    else:
        # Нет активного чата - показываем список ожидающих
        update.message.reply_text(
            "ℹ️ У вас нет активного чата.\n\n"
            "Используйте /start для просмотра списка ожидающих пользователей."
        )
        send_pending_users_list_to_operator(context, operator_id)


# ============================================================================
# ОБРАБОТЧИК ОШИБОК
# ============================================================================

def error_handler(update: Update, context: CallbackContext) -> None:
    """Обработчик ошибок"""
    logger.error(f"Update {update} caused error {context.error}")


# ============================================================================
# ГЛАВНАЯ ФУНКЦИЯ
# ============================================================================

def main() -> None:
    """Основная функция запуска бота"""
    if not BOT_TOKEN:
        logger.error("BOT_TOKEN не задан. Укажите токен в .env (BOT_TOKEN=...).")
        return

    # Создаем updater и dispatcher
    request_kwargs = {
        "connect_timeout": 20,
        "read_timeout": 20
    }
    if PROXY_URL:
        request_kwargs["proxy_url"] = PROXY_URL
        logger.info("Запуск через прокси: %s", PROXY_URL)

    updater = Updater(token=BOT_TOKEN, use_context=True, request_kwargs=request_kwargs)
    dispatcher = updater.dispatcher
    
    # Регистрируем обработчики команд
    dispatcher.add_handler(CommandHandler("start", start))
    dispatcher.add_handler(CommandHandler("help", help_command))
    
    # Обработчик callback от кнопок
    dispatcher.add_handler(CallbackQueryHandler(handle_operator_callback))
    
    # ConversationHandler для управления диалогами с оператором
    conv_handler = ConversationHandler(
        entry_points=[
            MessageHandler(Filters.regex('^💬 Связаться с оператором$'), request_operator)
        ],
        states={
            WAITING_FOR_OPERATOR: [
                MessageHandler(Filters.text & ~Filters.command, handle_user_message)
            ],
            OPERATOR_CHAT: [
                MessageHandler(Filters.text & ~Filters.command, handle_user_message)
            ],
        },
        fallbacks=[CommandHandler("start", start)],
    )
    dispatcher.add_handler(conv_handler)
    
    # Обработчик текстовых сообщений: админы — как операторы, остальные — как пользователи
    def dispatch_text_message(update: Update, context: CallbackContext) -> None:
        user_id = update.effective_user.id if update.effective_user else None
        if user_id is not None and user_id in ADMIN_IDS:
            handle_operator_message(update, context)
        else:
            handle_user_message(update, context)
    
    dispatcher.add_handler(MessageHandler(Filters.text & ~Filters.command, dispatch_text_message))
    
    # Обработчик ошибок
    dispatcher.add_error_handler(error_handler)
    
    # Запускаем бота
    logger.info("Бот запущен...")
    updater.start_polling()
    updater.idle()


if __name__ == '__main__':
    main()
