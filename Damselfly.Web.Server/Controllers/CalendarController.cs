using System.Security.Claims;
using Damselfly.Core.Constants;
using Damselfly.Core.DbModels.Models.API_Models;
using Damselfly.Core.ScopedServices.Interfaces;
using Damselfly.Core.Services;
using Google.Apis.Calendar.v3.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Damselfly.Web.Server.Controllers
{
    [ApiController]
    [Route("Calendar")]
    [Authorize(Policy = PolicyDefinitions.s_FireBaseAdmin)]
    public class CalendarController(
        GoogleCalendarService googleCalendarService,
        IAuthService authService
    ) : ControllerBase
    {
        private readonly GoogleCalendarService _googleCalendarService = googleCalendarService;
        private readonly IAuthService _authService = authService;

        /// <summary>
        /// Exchanges an authorization code for Google Calendar tokens
        /// </summary>
        /// <param name="request">The authorization code and user ID</param>
        /// <returns>Success status and any error messages</returns>
        [HttpPost("exchange-auth-code")]
        [ProducesResponseType(typeof(GoogleCalendarAuthResponse), 200)]
        public async Task<ActionResult<GoogleCalendarAuthResponse>> ExchangeAuthCode(
            [FromBody] GoogleCalendarAuthRequest request
        )
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            var user = await _authService.GetCurrentUser();
            if (user == null)
            {
                return Unauthorized("User not authenticated");
            }
            var result = await _googleCalendarService.ExchangeAuthCodeForTokensAsync(
                request.AuthCode,
                user.Id
            );

            if (result.Success)
            {
                return Ok(result);
            }
            else
            {
                return BadRequest(result);
            }
        }

        ///// <summary>
        ///// Creates a calendar event for the authenticated user
        ///// </summary>
        ///// <param name="eventRequest">The event details</param>
        ///// <returns>Success status and any error messages</returns>
        //[HttpPost("create-event")]
        //[ProducesResponseType(typeof(GoogleCalendarAuthResponse), 200)]
        //public async Task<IActionResult> CreateCalendarEvent([FromBody] CreateCalendarEventRequest eventRequest)
        //{
        //    if (!ModelState.IsValid)
        //    {
        //        return BadRequest(ModelState);
        //    }

        //    // Get the current user ID from the claims
        //    var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier);
        //    if (userIdClaim == null || !int.TryParse(userIdClaim.Value, out int userId))
        //    {
        //        return Unauthorized("User not authenticated");
        //    }

        //    var result = await _googleCalendarService.CreateCalendarEventAsync(userId, eventRequest);

        //    if (result.Success)
        //    {
        //        return Ok(result);
        //    }
        //    else
        //    {
        //        return BadRequest(result);
        //    }
        //}

        /// <summary>
        /// Checks if the current user has valid Google Calendar tokens
        /// </summary>
        /// <returns>Whether the user has valid tokens</returns>
        [HttpGet("has-valid-tokens")]
        [ProducesResponseType(typeof(BooleanResultModel), 200)]
        public async Task<ActionResult<BooleanResultModel>> HasValidTokens()
        {
            var currentUser = await _authService.GetCurrentUser();

            var hasValidTokens = await _googleCalendarService.HasValidTokensAsync(currentUser!.Id);

            return Ok(new BooleanResultModel { Result = hasValidTokens });
        }

        /// <summary>
        /// Revokes the Google Calendar tokens for the current user
        /// </summary>
        /// <returns>Success status</returns>
        [HttpPost("revoke-tokens")]
        public async Task<ActionResult<BooleanResultModel>> RevokeTokens()
        {
            var currentUser = await _authService.GetCurrentUser();

            var success = await _googleCalendarService.RevokeTokensAsync(currentUser!.Id);

            if (success)
            {
                return Ok(new BooleanResultModel { Result = true });
            }
            else
            {
                return BadRequest(new BooleanResultModel { Result = false });
            }
        }

        /// <summary>
        /// OAuth callback endpoint (for future use with web-based OAuth flow)
        /// </summary>
        /// <param name="code">The authorization code from Google</param>
        /// <param name="state">State parameter for security</param>
        /// <returns>Success page or error</returns>
        [HttpGet("callback")]
        public ActionResult<GoogleCalendarCallBackResponse> Callback(
            [FromQuery] string code,
            [FromQuery] string state
        )
        {
            if (string.IsNullOrEmpty(code))
            {
                return BadRequest("Authorization code is required");
            }

            // For now, return a simple success page
            // In a real implementation, you might want to redirect to a frontend page
            // or handle the code exchange here if the user is already authenticated
            return Ok(
                new GoogleCalendarCallBackResponse
                {
                    Message =
                        "Authorization code received successfully. Please use the exchange-auth-code endpoint to complete the process.",
                    Code = code,
                    State = state,
                }
            );
        }

        /// <summary>
        /// Returns the list of Google calendars for the authenticated user
        /// </summary>
        [HttpGet("list-calendars")]
        [ProducesResponseType(typeof(List<CalendarListEntry>), 200)]
        public async Task<ActionResult<List<CalendarListEntry>>> ListCalendars()
        {
            var currentUser = await _authService.GetCurrentUser();

            var calendars = await _googleCalendarService.GetCalendarsAsync(currentUser!.Id);
            if (calendars == null)
            {
                return Unauthorized("No valid Google Calendar tokens found for user");
            }

            // Return a simplified list (id, summary, description, primary)
            var result = calendars.Select(c => new
            {
                c.Id,
                c.Summary,
                c.Description,
                c.Primary,
            });
            return Ok(result);
        }

        /// <summary>
        /// Gets the user's Google Calendar settings
        /// </summary>
        [HttpGet("settings")]
        [ProducesResponseType(typeof(GoogleCalendarSettingsModel), 200)]
        public async Task<ActionResult<GoogleCalendarSettingsModel>> GetCalendarSettings()
        {
            var currentUser = await _authService.GetCurrentUser();

            var settings = await _googleCalendarService.GetCalendarSettingsAsync(currentUser!.Id);
            return Ok(settings);
        }

        /// <summary>
        /// Sets the user's Google Calendar settings
        /// </summary>
        [HttpPost("settings")]
        public async Task<ActionResult> SetCalendarSettings(
            [FromBody] GoogleCalendarSettingsModel settings
        )
        {
            var currentUser = await _authService.GetCurrentUser();
            var result = await _googleCalendarService.SetCalendarSettingsAsync(
                currentUser!.Id,
                settings
            );
            if (result)
                return Ok();
            return BadRequest();
        }
    }
}
