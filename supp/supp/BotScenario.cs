using System.Collections.Generic;

namespace supp
{
    public class BotScenario
    {
        public string botName { get; set; } = "Демо-бот";
        public string startStepId { get; set; } = "main";
        public string unknownMessage { get; set; } = "Я не понял сообщение. Выберите действие из меню.";
        public List<BotScenarioStep> steps { get; set; } = new List<BotScenarioStep>();
    }

    public class BotScenarioStep
    {
        public string id { get; set; }
        public string text { get; set; }
        public List<BotScenarioButton> buttons { get; set; } = new List<BotScenarioButton>();
    }

    public class BotScenarioButton
    {
        public string text { get; set; }
        public string action { get; set; } = "message";
        public string target { get; set; }
        public string message { get; set; }
    }
}
