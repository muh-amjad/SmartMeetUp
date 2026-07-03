using MeetUp.Api.Dtos.Auth;
using MeetUp.Api.Entities;
using MeetUp.Api.Repositories;
using MeetUp.Api.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace MeetUp.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AuthController : ControllerBase
    {
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IUserRepository _userRepository;
        private readonly IRefreshTokenRepository _refreshTokenRepository;
        private readonly ITokenService _tokenService;

        public AuthController(
            UserManager<ApplicationUser> userManager,
            IUserRepository userRepository,
            IRefreshTokenRepository refreshTokenRepository,
            ITokenService tokenService)
        {
            _userManager = userManager;
            _userRepository = userRepository;
            _refreshTokenRepository = refreshTokenRepository;
            _tokenService = tokenService;
        }

        [HttpPost("signup")]
        public async Task<ActionResult<AuthResponseDto>> Signup([FromBody] SignupRequestDto request, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
            {
                return BadRequest("Username, email and password are required.");
            }

            var existingByEmail = await _userManager.FindByEmailAsync(request.Email.Trim());
            if (existingByEmail is not null)
            {
                return Conflict("Email is already registered.");
            }

            var existingByUsername = await _userManager.FindByNameAsync(request.Username.Trim());
            if (existingByUsername is not null)
            {
                return Conflict("Username is already taken.");
            }

            var newUser = new ApplicationUser
            {
                UserName = request.Username.Trim(),
                Email = request.Email.Trim(),
                DisplayName = request.Username.Trim(),
            };

            var createResult = await _userManager.CreateAsync(newUser, request.Password);
            if (!createResult.Succeeded)
            {
                return BadRequest(createResult.Errors.Select(e => e.Description));
            }

            var response = await CreateAndPersistAuthResponse(newUser, cancellationToken);
            return Ok(response);
        }

        [HttpPost("login")]
        public async Task<ActionResult<AuthResponseDto>> Login([FromBody] LoginRequestDto request, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(request.UsernameOrEmail) || string.IsNullOrWhiteSpace(request.Password))
            {
                return BadRequest("Username/email and password are required.");
            }

            var user = await _userRepository.FindByUsernameOrEmailAsync(request.UsernameOrEmail, cancellationToken);
            if (user is null)
            {
                return Unauthorized("Invalid credentials.");
            }

            var validPassword = await _userManager.CheckPasswordAsync(user, request.Password);
            if (!validPassword)
            {
                return Unauthorized("Invalid credentials.");
            }

            var response = await CreateAndPersistAuthResponse(user, cancellationToken);
            return Ok(response);
        }

        [HttpPost("refresh")]
        public async Task<ActionResult<AuthResponseDto>> Refresh([FromBody] RefreshRequestDto request, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(request.RefreshToken))
            {
                return BadRequest("Refresh token is required.");
            }

            var existingToken = await _refreshTokenRepository.GetByTokenAsync(request.RefreshToken, cancellationToken);
            if (existingToken is null || !existingToken.IsActive)
            {
                return Unauthorized("Refresh token is invalid or expired.");
            }

            existingToken.RevokedAtUtc = DateTime.UtcNow;

            var response = await CreateAndPersistAuthResponse(existingToken.User, cancellationToken);
            return Ok(response);
        }

        [HttpPost("logout")]
        public async Task<IActionResult> Logout([FromBody] RefreshRequestDto request, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(request.RefreshToken))
            {
                return NoContent();
            }

            var existingToken = await _refreshTokenRepository.GetByTokenAsync(request.RefreshToken, cancellationToken);
            if (existingToken is null)
            {
                return NoContent();
            }

            await _refreshTokenRepository.RevokeUserTokensAsync(existingToken.UserId, cancellationToken);
            return NoContent();
        }

        private async Task<AuthResponseDto> CreateAndPersistAuthResponse(ApplicationUser user, CancellationToken cancellationToken)
        {
            var refreshTokenValue = _tokenService.GenerateRefreshToken();
            var refreshToken = new RefreshToken
            {
                Id = Guid.NewGuid(),
                Token = refreshTokenValue,
                UserId = user.Id,
                CreatedAtUtc = DateTime.UtcNow,
                ExpiresAtUtc = DateTime.UtcNow.AddDays(7),
            };

            await _refreshTokenRepository.AddAsync(refreshToken, cancellationToken);
            await _refreshTokenRepository.SaveChangesAsync(cancellationToken);

            return _tokenService.CreateAuthResponse(user, refreshTokenValue);
        }
    }
}
