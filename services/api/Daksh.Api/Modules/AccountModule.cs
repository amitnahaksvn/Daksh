using System.Security.Cryptography;
using System.Text;
using Daksh.Api.Auth;
using Daksh.Api.Infrastructure;
using FluentValidation;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.AspNetCore.Identity;
using MimeKit;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace Daksh.Api.Modules;

// First MongoDB-backed collections in this API: a registration is only written to `users` once its
// OTP is verified. `pendingRegistrations` holds the (already password-hashed) submission until then.

public sealed class ApplicationUser
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    public required string FirstName { get; set; }
    public required string LastName { get; set; }
    public required string Gender { get; set; }
    public DateTime DateOfBirth { get; set; }
    public required string Email { get; set; }
    public required string NormalizedEmail { get; set; }
    public bool EmailConfirmed { get; set; }
    public required string PasswordHash { get; set; }
    public string SecurityStamp { get; set; } = Guid.NewGuid().ToString("N");
    public string ConcurrencyStamp { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime CreatedAtUtc { get; set; }
    public bool LockoutEnabled { get; set; } = true;
    public DateTimeOffset? LockoutEnd { get; set; }
    public int AccessFailedCount { get; set; }
    /// <summary>"I'm busy" mode: auto-reply on every connected app while the owner is busy.</summary>
    public BusyProfile Busy { get; set; } = new();
}

public sealed class PendingRegistration
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    public required string Email { get; set; }
    public required string NormalizedEmail { get; set; }
    public required string FirstName { get; set; }
    public required string LastName { get; set; }
    public required string Gender { get; set; }
    public DateTime DateOfBirth { get; set; }
    public required string PasswordHash { get; set; }
    public required string OtpHash { get; set; }
    public DateTime OtpExpiresAtUtc { get; set; }
    public int Attempts { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

public sealed record RegisterRequest(string? FirstName, string? LastName, string? Gender, DateTime? DateOfBirth, string? Email, string? Password);
public sealed record VerifyOtpRequest(string? Email, string? Otp);
public sealed record AccountLoginRequest(string? Email, string? Password);
public sealed record AccountProfile(string Id, string FirstName, string LastName, string Email, string AccessToken, DateTimeOffset ExpiresAt);
public sealed record MessageResponse(string Message);
public sealed record UserDetails(string Id, string FirstName, string LastName, string Gender, DateTime DateOfBirth, string Email, DateTime CreatedAtUtc);
public sealed record UpdateProfileRequest(string? FirstName, string? LastName, string? Gender, DateTime? DateOfBirth);
public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);

public sealed class UpdateProfileRequestValidator : AbstractValidator<UpdateProfileRequest>
{
    public UpdateProfileRequestValidator()
    {
        RuleFor(x => x.FirstName).NotEmpty().MaximumLength(100);
        RuleFor(x => x.LastName).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Gender).NotEmpty().MaximumLength(50);
        RuleFor(x => x.DateOfBirth).NotNull()
            .Must(d => d is null || d < DateTime.UtcNow.Date).WithMessage("DateOfBirth must be in the past.")
            .Must(d => d is null || d <= DateTime.UtcNow.AddYears(-13)).WithMessage("You must be at least 13 years old.");
    }
}

public sealed class ChangePasswordRequestValidator : AbstractValidator<ChangePasswordRequest>
{
    public ChangePasswordRequestValidator()
    {
        RuleFor(x => x.CurrentPassword).NotEmpty();
        RuleFor(x => x.NewPassword).NotEmpty().MinimumLength(10).MaximumLength(200)
            .Matches("[A-Za-z]").WithMessage("Password must contain at least one letter.")
            .Matches("[0-9]").WithMessage("Password must contain at least one digit.");
    }
}

public sealed class RegisterRequestValidator : AbstractValidator<RegisterRequest>
{
    public RegisterRequestValidator()
    {
        RuleFor(x => x.FirstName).NotEmpty().MaximumLength(100);
        RuleFor(x => x.LastName).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Gender).NotEmpty().MaximumLength(50);
        RuleFor(x => x.DateOfBirth).NotNull()
            .Must(d => d < DateTime.UtcNow.Date).WithMessage("DateOfBirth must be in the past.")
            .Must(d => d is null || d <= DateTime.UtcNow.AddYears(-13)).WithMessage("You must be at least 13 years old.");
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(256);
        // Minimum length matches the agent platform's Auth:Policy:MinPasswordLength (10) for consistency.
        RuleFor(x => x.Password).NotEmpty().MinimumLength(10).MaximumLength(200)
            .Matches("[A-Za-z]").WithMessage("Password must contain at least one letter.")
            .Matches("[0-9]").WithMessage("Password must contain at least one digit.");
    }
}

public sealed class VerifyOtpRequestValidator : AbstractValidator<VerifyOtpRequest>
{
    public VerifyOtpRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress();
        RuleFor(x => x.Otp).NotEmpty().Length(6).Matches("^[0-9]{6}$").WithMessage("The code must be 6 digits.");
    }
}

public sealed class AccountLoginRequestValidator : AbstractValidator<AccountLoginRequest>
{
    public AccountLoginRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress();
        RuleFor(x => x.Password).NotEmpty();
    }
}

public interface IEmailSender
{
    Task SendAsync(string toAddress, string subject, string body, CancellationToken ct);
}

/// <summary>Sends mail through Gmail's SMTP relay using the credentials in configuration (Email:Gmail:*).</summary>
public sealed class GmailEmailSender(IConfiguration config, ILogger<GmailEmailSender> logger) : IEmailSender
{
    public async Task SendAsync(string toAddress, string subject, string body, CancellationToken ct)
    {
        var user = config["Email:Gmail:User"];
        var password = config["Email:Gmail:Password"];
        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException("Email:Gmail:User and Email:Gmail:Password must be configured.");

        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(user));
        message.To.Add(MailboxAddress.Parse(toAddress));
        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = body };

        using var client = new SmtpClient();
        // Some networks/sandboxes block the outbound OCSP/CRL lookup .NET performs during the TLS handshake,
        // which otherwise fails the connection with "An incomplete certificate revocation check occurred."
        // The certificate chain and hostname are still fully validated; only the revocation lookup is skipped.
        client.CheckCertificateRevocation = false;
        try
        {
            await client.ConnectAsync("smtp.gmail.com", 587, SecureSocketOptions.StartTls, ct);
            await client.AuthenticateAsync(user, password, ct);
            await client.SendAsync(message, ct);
        }
        catch (AuthenticationException ex)
        {
            // Gmail requires an App Password when 2-Step Verification is enabled; a normal account
            // password is rejected here ("less secure app access" no longer exists).
            logger.LogError(ex, "Gmail SMTP authentication failed for {User}", user);
            throw;
        }
        finally
        {
            if (client.IsConnected) await client.DisconnectAsync(true, ct);
        }
    }
}

public static class AccountModule
{
    private const int OtpLifetimeMinutes = 10;
    private const int MaxOtpAttempts = 5;
    private const int MaxFailedLogins = 10;
    private const int LockoutMinutes = 15;

    public static void MapAccount(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/account").WithTags("Account").AllowAnonymous();

        g.MapPost("/register", RegisterAsync).Validate<RegisterRequest>();
        g.MapPost("/verify-otp", VerifyOtpAsync).Validate<VerifyOtpRequest>();
        g.MapPost("/login", LoginAsync).Validate<AccountLoginRequest>();

        var me = app.MapGroup("/api/v1/account/me").WithTags("Account").RequireAuthorization(AuthSetup.UserScheme);
        me.MapGet("", GetMeAsync);
        me.MapPut("", UpdateMeAsync).Validate<UpdateProfileRequest>();
        me.MapPost("/password", ChangePasswordAsync).Validate<ChangePasswordRequest>();
        me.MapGet("/busy", GetBusyAsync);
        me.MapPut("/busy", UpdateBusyAsync);
    }

    private static BusyProfileDto ToBusyDto(BusyProfile b) =>
        new(b.Enabled, b.Timezone, b.Start, b.End, b.Days, b.Until, b.ReplyMode, b.FixedMessage, BusyMode.IsBusy(b, DateTime.UtcNow));

    private static async Task<IResult> GetBusyAsync(IMongoDatabase db, ClaimsPrincipalAccessor who, CancellationToken ct) =>
        Results.Ok(ToBusyDto(await BusyMode.LoadAsync(db, who.UserId, ct) ?? new BusyProfile()));

    private static async Task<IResult> UpdateBusyAsync(UpdateBusyProfileRequest req, IMongoDatabase db, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        var users = db.GetCollection<ApplicationUser>("users");
        var user = await users.Find(u => u.Id == who.UserId).FirstOrDefaultAsync(ct);
        if (user is null) return Results.NotFound();
        var b = user.Busy ?? new BusyProfile();

        if (req.Timezone is { } tz)
        {
            if (tz.Length > 0 && !BusyMode.IsValidTimezone(tz)) return Results.Problem(statusCode: 400, title: "Unknown timezone. Use a name like Asia/Kolkata.");
            b.Timezone = tz;
        }
        if (req.Start is { } start) { if (!TimeSpan.TryParse(start, out _)) return Results.Problem(statusCode: 400, title: "Busy start must be a time like 09:00."); b.Start = start; }
        if (req.End is { } end) { if (!TimeSpan.TryParse(end, out _)) return Results.Problem(statusCode: 400, title: "Busy end must be a time like 18:00."); b.End = end; }
        if (req.Days is { } days) b.Days = days.Where(d => d is >= 0 and <= 6).Distinct().Order().ToList();
        if (req.Until is { } until) b.Until = until.ToUniversalTime();
        if (req.ClearUntil == true) b.Until = null;
        if (req.ReplyMode is { } mode) b.ReplyMode = mode == "Fixed" ? "Fixed" : "Ai";
        if (req.FixedMessage is { } msg) b.FixedMessage = msg.Length > 1000 ? msg[..1000] : msg;
        // Starting a busy period from the quick button also switches the mode on.
        if (req.Enabled is { } enabled) b.Enabled = enabled;
        else if (req.Until is not null) b.Enabled = true;

        await users.UpdateOneAsync(u => u.Id == who.UserId, Builders<ApplicationUser>.Update.Set(u => u.Busy, b), cancellationToken: ct);
        return Results.Ok(ToBusyDto(b));
    }

    private static UserDetails ToDetails(ApplicationUser u) =>
        new(u.Id, u.FirstName, u.LastName, u.Gender, DateTime.SpecifyKind(u.DateOfBirth, DateTimeKind.Utc), u.Email, u.CreatedAtUtc);

    private static async Task<IResult> GetMeAsync(IMongoDatabase db, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        var user = await db.GetCollection<ApplicationUser>("users").Find(u => u.Id == who.UserId).FirstOrDefaultAsync(ct);
        return user is null ? Results.NotFound() : Results.Ok(ToDetails(user));
    }

    private static async Task<IResult> UpdateMeAsync(UpdateProfileRequest req, IMongoDatabase db, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        var users = db.GetCollection<ApplicationUser>("users");
        var update = Builders<ApplicationUser>.Update
            .Set(u => u.FirstName, req.FirstName!.Trim())
            .Set(u => u.LastName, req.LastName!.Trim())
            .Set(u => u.Gender, req.Gender!.Trim())
            .Set(u => u.DateOfBirth, DateTime.SpecifyKind(req.DateOfBirth!.Value.Date, DateTimeKind.Utc));
        var user = await users.FindOneAndUpdateAsync(u => u.Id == who.UserId, update,
            new FindOneAndUpdateOptions<ApplicationUser> { ReturnDocument = ReturnDocument.After }, ct);
        return user is null ? Results.NotFound() : Results.Ok(ToDetails(user));
    }

    private static async Task<IResult> ChangePasswordAsync(ChangePasswordRequest req, IMongoDatabase db, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        var users = db.GetCollection<ApplicationUser>("users");
        var user = await users.Find(u => u.Id == who.UserId).FirstOrDefaultAsync(ct);
        if (user is null) return Results.NotFound();

        var hasher = new PasswordHasher<ApplicationUser>();
        if (hasher.VerifyHashedPassword(user, user.PasswordHash, req.CurrentPassword!) == PasswordVerificationResult.Failed)
            return Results.Problem(statusCode: 400, title: "Current password is incorrect.");

        await users.UpdateOneAsync(u => u.Id == user.Id, Builders<ApplicationUser>.Update
            .Set(u => u.PasswordHash, hasher.HashPassword(user, req.NewPassword!))
            .Set(u => u.SecurityStamp, Guid.NewGuid().ToString("N")), cancellationToken: ct);
        return Results.Ok(new MessageResponse("Password updated."));
    }

    /// <summary>Creates the unique/TTL indexes this module depends on. Idempotent — safe to call on every startup.</summary>
    public static async Task EnsureAccountIndexesAsync(IMongoDatabase database, CancellationToken ct = default)
    {
        var users = database.GetCollection<ApplicationUser>("users");
        await users.Indexes.CreateOneAsync(
            new CreateIndexModel<ApplicationUser>(
                Builders<ApplicationUser>.IndexKeys.Ascending(u => u.NormalizedEmail),
                new CreateIndexOptions { Unique = true, Name = "ux_users_normalizedEmail" }),
            cancellationToken: ct);

        var pending = database.GetCollection<PendingRegistration>("pendingRegistrations");
        await pending.Indexes.CreateOneAsync(
            new CreateIndexModel<PendingRegistration>(
                Builders<PendingRegistration>.IndexKeys.Ascending(p => p.NormalizedEmail),
                new CreateIndexOptions { Unique = true, Name = "ux_pendingRegistrations_normalizedEmail" }),
            cancellationToken: ct);
        await pending.Indexes.CreateOneAsync(
            new CreateIndexModel<PendingRegistration>(
                Builders<PendingRegistration>.IndexKeys.Ascending(p => p.OtpExpiresAtUtc),
                new CreateIndexOptions { Name = "ttl_pendingRegistrations_otpExpiresAtUtc", ExpireAfter = TimeSpan.Zero }),
            cancellationToken: ct);
    }

    private static async Task<IResult> RegisterAsync(RegisterRequest req, IMongoDatabase db, IEmailSender email, CancellationToken ct)
    {
        var normalizedEmail = req.Email!.Trim().ToUpperInvariant();
        var users = db.GetCollection<ApplicationUser>("users");
        if (await users.Find(u => u.NormalizedEmail == normalizedEmail).AnyAsync(ct))
            return Results.Problem(statusCode: 409, title: "An account with this email already exists.");

        var hasher = new PasswordHasher<ApplicationUser>();
        var passwordHash = hasher.HashPassword(null!, req.Password!);

        var otp = GenerateOtp();
        var pending = new PendingRegistration
        {
            Email = req.Email!.Trim(),
            NormalizedEmail = normalizedEmail,
            FirstName = req.FirstName!.Trim(),
            LastName = req.LastName!.Trim(),
            Gender = req.Gender!.Trim(),
            DateOfBirth = DateTime.SpecifyKind(req.DateOfBirth!.Value.Date, DateTimeKind.Utc),
            PasswordHash = passwordHash,
            OtpHash = HashOtp(otp),
            OtpExpiresAtUtc = DateTime.UtcNow.AddMinutes(OtpLifetimeMinutes),
            Attempts = 0,
            CreatedAtUtc = DateTime.UtcNow,
        };

        // Field-level update (not ReplaceOneAsync) so a re-registration never touches the existing
        // document's immutable _id — only ReplaceOneAsync's full-document replace would hit that conflict.
        var pendingCollection = db.GetCollection<PendingRegistration>("pendingRegistrations");
        var update = Builders<PendingRegistration>.Update
            .Set(p => p.Email, pending.Email)
            .Set(p => p.NormalizedEmail, pending.NormalizedEmail)
            .Set(p => p.FirstName, pending.FirstName)
            .Set(p => p.LastName, pending.LastName)
            .Set(p => p.Gender, pending.Gender)
            .Set(p => p.DateOfBirth, pending.DateOfBirth)
            .Set(p => p.PasswordHash, pending.PasswordHash)
            .Set(p => p.OtpHash, pending.OtpHash)
            .Set(p => p.OtpExpiresAtUtc, pending.OtpExpiresAtUtc)
            .Set(p => p.Attempts, pending.Attempts)
            .Set(p => p.CreatedAtUtc, pending.CreatedAtUtc);
        await pendingCollection.UpdateOneAsync(p => p.NormalizedEmail == normalizedEmail, update, new UpdateOptions { IsUpsert = true }, ct);

        try
        {
            await email.SendAsync(
                pending.Email,
                "Your Daksh verification code",
                $"Your verification code is {otp}. It expires in {OtpLifetimeMinutes} minutes. If you did not request this, you can ignore this email.",
                ct);
        }
        catch (Exception ex)
        {
            return Results.Problem(statusCode: 502, title: "Could not send the verification email. Please try again.", detail: ex.Message);
        }

        return Results.Ok(new MessageResponse("Verification code sent to your email."));
    }

    private static async Task<IResult> VerifyOtpAsync(VerifyOtpRequest req, IMongoDatabase db, UserTokenService tokens, CancellationToken ct)
    {
        var normalizedEmail = req.Email!.Trim().ToUpperInvariant();
        var pendingCollection = db.GetCollection<PendingRegistration>("pendingRegistrations");
        var pending = await pendingCollection.Find(p => p.NormalizedEmail == normalizedEmail).FirstOrDefaultAsync(ct);

        if (pending is null || pending.OtpExpiresAtUtc < DateTime.UtcNow)
            return Results.Problem(statusCode: 400, title: "Verification code expired or not found. Please register again.");

        if (pending.OtpHash != HashOtp(req.Otp!))
        {
            var attempts = pending.Attempts + 1;
            if (attempts >= MaxOtpAttempts)
            {
                await pendingCollection.DeleteOneAsync(p => p.Id == pending.Id, ct);
                return Results.Problem(statusCode: 400, title: "Too many incorrect attempts. Please register again.");
            }
            await pendingCollection.UpdateOneAsync(p => p.Id == pending.Id, Builders<PendingRegistration>.Update.Set(p => p.Attempts, attempts), cancellationToken: ct);
            return Results.Problem(statusCode: 400, title: $"Incorrect verification code. {MaxOtpAttempts - attempts} attempt(s) remaining.");
        }

        var user = new ApplicationUser
        {
            FirstName = pending.FirstName,
            LastName = pending.LastName,
            Gender = pending.Gender,
            DateOfBirth = pending.DateOfBirth,
            Email = pending.Email,
            NormalizedEmail = pending.NormalizedEmail,
            EmailConfirmed = true,
            PasswordHash = pending.PasswordHash,
            CreatedAtUtc = DateTime.UtcNow,
            LockoutEnabled = true,
            AccessFailedCount = 0,
        };

        var users = db.GetCollection<ApplicationUser>("users");
        try
        {
            await users.InsertOneAsync(user, cancellationToken: ct);
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            await pendingCollection.DeleteOneAsync(p => p.Id == pending.Id, ct);
            return Results.Problem(statusCode: 409, title: "An account with this email already exists.");
        }

        await pendingCollection.DeleteOneAsync(p => p.Id == pending.Id, ct);

        var (token, expiresAt) = tokens.CreateAccessToken(user.Id, user.Email);
        return Results.Ok(new AccountProfile(user.Id, user.FirstName, user.LastName, user.Email, token, expiresAt));
    }

    private static async Task<IResult> LoginAsync(AccountLoginRequest req, IMongoDatabase db, UserTokenService tokens, CancellationToken ct)
    {
        var normalizedEmail = req.Email!.Trim().ToUpperInvariant();
        var users = db.GetCollection<ApplicationUser>("users");
        var user = await users.Find(u => u.NormalizedEmail == normalizedEmail).FirstOrDefaultAsync(ct);

        // Same generic message whether the email is unknown or the password is wrong, so neither is leaked.
        const string invalidCredentials = "Invalid email or password.";
        if (user is null)
            return Results.Problem(statusCode: 401, title: invalidCredentials);

        if (user.LockoutEnabled && user.LockoutEnd is { } until && until > DateTimeOffset.UtcNow)
            return Results.Problem(statusCode: 401, title: $"Account is locked until {until:u}.");

        var hasher = new PasswordHasher<ApplicationUser>();
        var verification = hasher.VerifyHashedPassword(user, user.PasswordHash, req.Password!);
        if (verification == PasswordVerificationResult.Failed)
        {
            var failedCount = user.AccessFailedCount + 1;
            var update = Builders<ApplicationUser>.Update.Set(u => u.AccessFailedCount, failedCount);
            if (failedCount >= MaxFailedLogins)
                update = Builders<ApplicationUser>.Update.Combine(
                    Builders<ApplicationUser>.Update.Set(u => u.AccessFailedCount, 0),
                    Builders<ApplicationUser>.Update.Set(u => u.LockoutEnd, DateTimeOffset.UtcNow.AddMinutes(LockoutMinutes)));
            await users.UpdateOneAsync(u => u.Id == user.Id, update, cancellationToken: ct);
            return Results.Problem(statusCode: 401, title: invalidCredentials);
        }

        if (user.AccessFailedCount != 0)
            await users.UpdateOneAsync(u => u.Id == user.Id, Builders<ApplicationUser>.Update.Set(u => u.AccessFailedCount, 0), cancellationToken: ct);

        var (token, expiresAt) = tokens.CreateAccessToken(user.Id, user.Email);
        return Results.Ok(new AccountProfile(user.Id, user.FirstName, user.LastName, user.Email, token, expiresAt));
    }

    private static string GenerateOtp() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    private static string HashOtp(string otp) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(otp)));
}
