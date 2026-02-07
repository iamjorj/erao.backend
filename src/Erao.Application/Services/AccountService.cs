using AutoMapper;
using Erao.Core.DTOs;
using Erao.Core.Interfaces;

namespace Erao.Application.Services;

public interface IAccountService
{
    Task<UserDto?> GetAccountAsync(Guid userId);
    Task<UserDto> UpdateAccountAsync(Guid userId, string? firstName, string? lastName);
}

public class AccountService : IAccountService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IMapper _mapper;

    public AccountService(IUnitOfWork unitOfWork, IMapper mapper)
    {
        _unitOfWork = unitOfWork;
        _mapper = mapper;
    }

    public async Task<UserDto?> GetAccountAsync(Guid userId)
    {
        var user = await _unitOfWork.Users.GetByIdAsync(userId);
        if (user == null)
        {
            return null;
        }
        return _mapper.Map<UserDto>(user);
    }

    public async Task<UserDto> UpdateAccountAsync(Guid userId, string? firstName, string? lastName)
    {
        var user = await _unitOfWork.Users.GetByIdAsync(userId);
        if (user == null)
        {
            throw new InvalidOperationException("User not found");
        }

        // Update fields - use null check to allow clearing values with empty string
        if (firstName != null)
            user.FirstName = firstName;
        if (lastName != null)
            user.LastName = lastName;

        // Mark entity as modified and save
        await _unitOfWork.Users.UpdateAsync(user);
        await _unitOfWork.SaveChangesAsync();

        return _mapper.Map<UserDto>(user);
    }
}
