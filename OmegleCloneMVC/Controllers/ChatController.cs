using Microsoft.AspNetCore.Mvc;

namespace OmegleCloneMVC.Controllers
{
    public class ChatController : Controller
    {
        private readonly IConfiguration _config;

        public ChatController(IConfiguration config)
        {
            _config = config;
        }

        public IActionResult Video() => View("Index");

        public IActionResult Text() => View("TextChat");

        [HttpGet("/api/ice-config")]
        public IActionResult IceConfig()
        {
            var iceServers = new List<object>
            {
                new { urls = new[] { "stun:stun.l.google.com:19302" } }
            };

            var turnUrls = _config.GetSection("WebRTC:TurnUrls").Get<string[]>();
            var turnUsername = _config["WebRTC:TurnUsername"];
            var turnCredential = _config["WebRTC:TurnCredential"];

            if (turnUrls?.Length > 0 && !string.IsNullOrWhiteSpace(turnUsername))
            {
                iceServers.Add(new
                {
                    urls = turnUrls,
                    username = turnUsername,
                    credential = turnCredential
                });
            }

            return Ok(new { iceServers });
        }
    }
}
