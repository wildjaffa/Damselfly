using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Damselfly.Core.Constants;
using Damselfly.Core.DbModels.Authentication;
using Damselfly.Core.DbModels.Models.APIModels;
using Damselfly.Core.ScopedServices.Interfaces;
using Damselfly.Core.Services;
using Damselfly.Core.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

// Avoid namespace clashes with IAuthorizationService extension methods

namespace Damselfly.Core.ScopedServices;

/// <summary>
///     User service to manage users and roles. We try and keep each user to a distinct
///     role - so they're either an Admin, User or ReadOnly. Roles can be combinatorial
///     but it's simpler to have a single role per user.
/// </summary>
public class UserManagementService : IUserMgmtService
{
    
    private readonly ConfigService _configService;
    private readonly RoleManager<ApplicationRole> _roleManager;
    private readonly UserManager<AppIdentityUser> _userManager;
    private readonly IConfiguration _config;

    public UserManagementService(RoleManager<ApplicationRole> roleManager,
        UserManager<AppIdentityUser> userManager,
        ConfigService configService,
        IAuthorizationService authService,
        IConfiguration config)
    {
        _userManager = userManager;
        _roleManager = roleManager;
        _configService = configService;
        _config = config;
    }

    public bool RolesEnabled => _configService.GetBool(ConfigSettings.EnablePoliciesAndRoles, true);

    public bool AllowPublicRegistration => _configService.GetBool(ConfigSettings.AllowExternalRegistration);

    /// <summary>
    ///     Gets the list of users currently registered
    /// </summary>
    /// <returns></returns>
    public async Task<ICollection<AppIdentityUser>> GetUsers()
    {
        var users = await _userManager.Users
            .Include(x => x.UserRoles)
            .ThenInclude(y => y.Role)
            .ToListAsync();
        return users;
    }

    /// <summary>
    ///     Gets the list of users currently registered
    /// </summary>
    /// <returns></returns>
    public async Task<AppIdentityUser> GetUserByName( string userName )
    {
        var user = await _userManager.Users
            .Where( x => x.UserName == userName )
            .Include( x => x.UserRoles )
            .ThenInclude( y => y.Role )
            .FirstOrDefaultAsync();
        return user;
    }

    /// <summary>
    ///     Gets the list of users currently registered
    /// </summary>
    /// <returns></returns>
    public async Task<AppIdentityUser> GetUser(int userId )
    {
        var user = await _userManager.Users
            .Where( x => x.Id == userId )
            .Include(x => x.UserRoles)
            .ThenInclude(y => y.Role)
            .FirstOrDefaultAsync();
        return user;
    }

    /// <summary>
    ///     Gets the list of roles configured in the system
    /// </summary>
    /// <returns></returns>
    public async Task<ICollection<ApplicationRole>> GetRoles()
    {
        var roles = await _roleManager.Roles.ToListAsync();
        return roles;
    }

    /// <summary>
    ///     For newly registered users, check the role they're in
    ///     and add them to the default role(s).
    /// </summary>
    /// <param name="user"></param>
    /// <returns></returns>
    public async Task AddUserToDefaultRoles(AppIdentityUser user)
    {
        // First, if they're the first user in the DB, make them Admin
        await CheckAdminUser();

        var userRoles = await _userManager.GetRolesAsync(user);

        if ( !userRoles.Any() )
            // If the user isn't a member of other roles (i.e., they haven't
            // been added to Admin) then make them a 'user'.
            await _userManager.AddToRoleAsync(user, RoleDefinitions.s_UserRole);
    }

    /// <summary>
    ///     Updates a user's properties and syncs their roles.
    /// </summary>
    /// <param name="user"></param>
    /// <param name="newRoleSet"></param>
    /// <returns></returns>
    public async Task<UserResponse> UpdateUserAsync(string userName, string emailAddress, ICollection<string> newRoles)
    {
        var user = await GetUserByName( userName );

        if( user != null )
        {
            user.Email = emailAddress;
            var result = await _userManager.UpdateAsync( user );

            if( result.Succeeded )
            {
                var syncResult = await SyncUserRoles( user, newRoles, false );

                if( syncResult != null )
                    // Non-null result means we did something and it succeeded or failed.
                    result = syncResult;
            }

            return new UserResponse( result );
        }

        return new UserResponse( IdentityResult.Failed() );
    }

    /// <summary>
    ///     Reset the user password
    /// </summary>
    /// <param name="user"></param>
    /// <param name="password">Unhashed password</param>
    /// <returns></returns>
    public async Task<UserResponse> SetUserPasswordAsync(string userName, string password)
    {
        var user = await GetUserByName( userName );
        var token = await _userManager.GeneratePasswordResetTokenAsync(user);

        return new UserResponse( await _userManager.ResetPasswordAsync(user, token, password) );
    }

    public async Task<UserResponse> CreateNewUser(string userName, string email, string password,
        ICollection<string>? roles = null)
    {

        var newUser = new AppIdentityUser { UserName = userName, Email = email };

        var result = await _userManager.CreateAsync(newUser, password);

        if ( result.Succeeded )
        {
            Logging.Log("User created a new account with password.");

            if ( roles == null || !roles.Any() )
                await AddUserToDefaultRoles(newUser);
            else
                await SyncUserRoles(newUser, roles, false);
        }

        return new UserResponse( result );
    }

    public /*async*/ Task<string> GetUserPasswordResetLink(AppIdentityUser user)
    {
        // http://localhost:6363/Identity/Account/ResetPassword?user=12345&code=2134234
        throw new NotImplementedException();
        /* Something like.... 
        var user = await UserManager.FindByNameAsync(model.Email);
        if (user == null || !(await UserManager.IsEmailConfirmedAsync(user.Id)))
        {
            // Don't reveal that the user does not exist or is not confirmed
            return View("ForgotPasswordConfirmation");
        }

        var code = await UserManager.GeneratePasswordResetTokenAsync(user.Id);
        var callbackUrl = Url.Action("ResetPassword", "Account",
                    new { UserId = user.Id, code = code }, protocol: Request.Url.Scheme);
        await UserManager.SendEmailAsync(user.Id, "Reset Password",
                        "Please reset your password by clicking here: <a href=\"" + callbackUrl + "\">link</a>");
        return View("ForgotPasswordConfirmation");
        */
    }

    /// <summary>
    ///     If there are no admin users, make the Admin with the lowest ID
    ///     an admin.
    ///     TODO: This is a bit arbitrary, and whilst a reasonable fallback
    ///     it's not robust from a security perspective. A better option might
    ///     be to fail at startup, and provide a command-line option to set
    ///     a user to admin based on email address.
    /// </summary>
    /// <param name="userManager"></param>
    /// <returns></returns>
    public async Task CheckAdminUser()
    {
        try
        {
            // First, check if there's any users at all yet.
            var users = _userManager.Users.ToList();

            if ( users.Any() )
            {
                // If we have users, see if any are Admins.
                var adminUsers = await _userManager.GetUsersInRoleAsync(RoleDefinitions.s_AdminRole);

                if ( !adminUsers.Any() )
                {
                    // For the moment, arbitrarily promote the first user to admin
                    var user = users.MinBy(x => x.Id);

                    if ( user != null )
                    {
                        Logging.Log(
                            $"No user found with {RoleDefinitions.s_AdminRole} role. Adding user {user.UserName} to that role.");

                        // Put admin in Administrator role
                        var result = await _userManager.AddToRoleAsync(user, RoleDefinitions.s_AdminRole);

                        if ( result.Succeeded )
                            // Remove the other roles from the users
                            await _userManager.RemoveFromRolesAsync(user,
                                new List<string> { RoleDefinitions.s_ReadOnlyRole, RoleDefinitions.s_UserRole });
                    }
                    else
                    {
                        Logging.LogWarning(
                            $"No user found that could be promoted to {RoleDefinitions.s_AdminRole} role.");
                    }
                }
            }
        }
        catch ( Exception ex )
        {
            Logging.LogError($"Unexpected exception while checking Admin role members: {ex}");
        }
    }

    /// <summary>
    ///     Syncs the user's roles to the set of roles passed in. Note that
    ///     if the 'Admin' role is removed, and there are no other admin users
    ///     in the system, the user won't be removed from the Admin roles, to
    ///     ensure we always have at least one Admin.
    ///     Note that this method works for multiple roles, but we only want
    ///     to have users with one role at a time.
    /// </summary>
    /// <param name="user"></param>
    /// <param name="newRoles"></param>
    /// <returns></returns>
    public async Task<IdentityResult> SyncUserRoles(AppIdentityUser user, ICollection<string> newRoles, bool addOnly)
    {
        // Assume success until proven otherwise. 
        var result = IdentityResult.Success;

        var roles = await _userManager.GetRolesAsync(user);

        var rolesToAdd = newRoles.Except(roles);

        // Is this a full sync? Or just adding new roles?
        var rolesToRemove = Enumerable.Empty<string>();
        if ( !addOnly )
            rolesToRemove = roles.Except(newRoles);

        var errorMsg = string.Empty;

        if ( rolesToRemove.Contains(RoleDefinitions.s_AdminRole) )
        {
            // Don't remove from Admin unless there's another admin
            var adminUsers = await _userManager.GetUsersInRoleAsync(RoleDefinitions.s_AdminRole);

            if ( adminUsers.Count <= 1 )
            {
                rolesToRemove = rolesToRemove.Except(new List<string> { RoleDefinitions.s_AdminRole });
                errorMsg = $" Please ensure one other user has '{RoleDefinitions.s_AdminRole}'.";
            }
        }

        string changes = string.Empty, prefix = string.Empty;

        if ( rolesToRemove.Any() )
        {
            prefix = $"User {user.UserName} ";
            result = await _userManager.RemoveFromRolesAsync(user, rolesToRemove);

            if (result.Succeeded)
            {
                errorMsg = $"role removal failed: {result.Errors}";
            }
            else
                changes = $"removed from {string.Join(", ", rolesToRemove.Select(x => "'x'"))} roles";
        }

        if ( result.Succeeded && rolesToAdd.Any() )
        {
            prefix = $"User {user.UserName} ";
            result = await _userManager.AddToRolesAsync(user, rolesToAdd);

            if ( !string.IsNullOrEmpty(changes) ) changes += " and ";

            if (!result.Succeeded)
            {
                errorMsg = $"role addition failed: {result.Errors}";
            }
            else
                changes += $"added to {string.Join(", ", rolesToAdd.Select(x => $"'{x}'"))} roles";
        }

        if ( !string.IsNullOrEmpty(changes) )
            changes += ". ";
        
        if( ! string.IsNullOrEmpty( errorMsg ))
            Logging.LogError($"SyncUserRoles: {prefix}{changes}{errorMsg}");
        else
            Logging.Log($"SyncUserRoles: {prefix}{changes}");

        return result;
    }


    public async Task<AppIdentityUser> GetOrCreateUser(ClaimsPrincipal user)
    {
        var userEmail = user.Claims.FirstOrDefault(x => x.Type == DamselflyContants.EmailClaim)?.Value;
        var appUser = await _userManager.FindByEmailAsync(userEmail);
        if (appUser == null)
        {
            var adminEmails = _config["AdminEmails"].Split(',');
            var newUser = new AppIdentityUser
            {
                UserName = userEmail,
                Email = userEmail,
                
            };
            var createResult = await _userManager.CreateAsync(newUser);
            appUser = await _userManager.FindByEmailAsync(user.Claims.FirstOrDefault(x => x.Type == DamselflyContants.EmailClaim)?.Value);
            if (adminEmails.Contains(userEmail))
                await _userManager.AddToRolesAsync(newUser, [RoleDefinitions.s_AdminRole]);
        }
        return appUser;
    }
}