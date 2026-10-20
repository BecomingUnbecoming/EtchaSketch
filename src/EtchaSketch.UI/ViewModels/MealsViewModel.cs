using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EtchaSketch.Core.Pantry;
using EtchaSketch.Core.Profiles;
using EtchaSketch.UI.Services;

namespace EtchaSketch.UI.ViewModels;

/// <summary>The week's meals and the household's recipes.</summary>
public partial class MealsViewModel(AppSession session, Household household, Action changed) : ViewModelBase
{
    private Guid? _editingRecipe;

    public ObservableCollection<DayMealsViewModel> Week { get; } = [];

    public ObservableCollection<RecipeRowViewModel> Recipes { get; } = [];

    public IReadOnlyList<Meal> MealKinds { get; } = Enum.GetValues<Meal>();

    public ObservableCollection<RecipeChoice> RecipeChoices { get; } = [];

    [ObservableProperty]
    public partial DateTime? PlanDay { get; set; }

    [ObservableProperty]
    public partial Meal PlanMeal { get; set; } = Meal.Dinner;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsTitle))]
    public partial RecipeChoice? PlanRecipe { get; set; }

    /// <summary>What's planned, when it isn't one of the recipes (like "Takeaway").</summary>
    [ObservableProperty]
    public partial string? PlanTitle { get; set; }

    public bool NeedsTitle => PlanRecipe?.RecipeId is null;

    [ObservableProperty]
    public partial string? RecipeName { get; set; }

    [ObservableProperty]
    public partial decimal? RecipeServings { get; set; }

    /// <summary>One ingredient per line, like "200 g rice" or "2 eggs".</summary>
    [ObservableProperty]
    public partial string? RecipeIngredients { get; set; }

    [ObservableProperty]
    public partial string? RecipeInstructions { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RecipeSaveLabel))]
    public partial bool IsEditingRecipe { get; private set; }

    public string RecipeSaveLabel => IsEditingRecipe ? "Save recipe" : "Add recipe";

    [ObservableProperty]
    public partial string? Message { get; private set; }

    public void Refresh()
    {
        var today = session.Today;
        PlanDay ??= today.ToDateTime(TimeOnly.MinValue);

        var recipes = session.Meals.Recipes(household.Id);
        var chosen = PlanRecipe?.RecipeId;
        RecipeChoices.Clear();
        RecipeChoices.Add(new RecipeChoice("No recipe (type what's planned)", null));
        foreach (var recipe in recipes)
        {
            RecipeChoices.Add(new RecipeChoice(recipe.Name, recipe.Id));
        }

        PlanRecipe = RecipeChoices.FirstOrDefault(choice => choice.RecipeId == chosen) ?? RecipeChoices[0];

        Recipes.Clear();
        foreach (var recipe in recipes)
        {
            Recipes.Add(new RecipeRowViewModel(recipe, () => EditRecipe(recipe), () => DeleteRecipe(recipe)));
        }

        Week.Clear();
        var meals = session.Meals.Meals(household.Id, today, today.AddDays(6));
        for (var day = today; day <= today.AddDays(6); day = day.AddDays(1))
        {
            var label = day == today ? "Today" : day == today.AddDays(1) ? "Tomorrow" : day.ToString("dddd d MMM", CultureInfo.CurrentCulture);
            Week.Add(new DayMealsViewModel(label, meals.Where(meal => meal.Day == day)
                .Select(meal => new MealRowViewModel(meal, () => Cook(meal), () => Remove(meal))).ToList()));
        }
    }

    [RelayCommand]
    private void Plan()
    {
        try
        {
            var (meal, added) = session.Meals.Plan(new PlannedMeal
            {
                HouseholdId = household.Id,
                Day = DateOnly.FromDateTime(PlanDay ?? session.Today.ToDateTime(TimeOnly.MinValue)),
                Meal = PlanMeal,
                RecipeId = PlanRecipe?.RecipeId,
                Title = PlanTitle ?? "",
            });
            Message = added.Count == 0
                ? $"{meal.Title} is planned."
                : $"{meal.Title} is planned. Added to the shopping list: {string.Join(", ", added.Select(line => line.Name))}.";
            PlanTitle = null;
            changed();
        }
        catch (ArgumentException exception)
        {
            Message = Messages.Friendly(exception);
        }
    }

    [RelayCommand]
    private void SaveRecipe()
    {
        try
        {
            var recipe = new Recipe
            {
                Id = _editingRecipe ?? Guid.NewGuid(),
                HouseholdId = household.Id,
                Name = RecipeName ?? "",
                Servings = RecipeServings is { } servings ? (int)servings : null,
                Instructions = RecipeInstructions,
                Ingredients = (RecipeIngredients ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(Ingredient.Parse).ToList(),
            };
            if (_editingRecipe is null)
            {
                session.Meals.AddRecipe(recipe);
            }
            else
            {
                session.Meals.UpdateRecipe(recipe);
            }

            Message = null;
            ClearRecipeForm();
            changed();
        }
        catch (ArgumentException exception)
        {
            Message = Messages.Friendly(exception);
        }
    }

    [RelayCommand]
    private void CancelRecipe() => ClearRecipeForm();

    private void EditRecipe(Recipe recipe)
    {
        _editingRecipe = recipe.Id;
        RecipeName = recipe.Name;
        RecipeServings = recipe.Servings;
        RecipeIngredients = string.Join('\n', recipe.Ingredients.Select(ingredient => ingredient.ToString()));
        RecipeInstructions = recipe.Instructions;
        IsEditingRecipe = true;
    }

    private void DeleteRecipe(Recipe recipe)
    {
        session.Meals.DeleteRecipe(recipe.Id);
        changed();
    }

    private void Cook(PlannedMeal meal)
    {
        session.Meals.Cook(meal.Id, session.Now);
        changed();
    }

    private void Remove(PlannedMeal meal)
    {
        session.Meals.RemoveMeal(meal.Id);
        changed();
    }

    private void ClearRecipeForm()
    {
        _editingRecipe = null;
        RecipeName = null;
        RecipeServings = null;
        RecipeIngredients = null;
        RecipeInstructions = null;
        IsEditingRecipe = false;
    }
}

public sealed record RecipeChoice(string Label, Guid? RecipeId)
{
    public override string ToString() => Label;
}

public sealed record DayMealsViewModel(string Day, IReadOnlyList<MealRowViewModel> Meals)
{
    public bool IsEmpty => Meals.Count == 0;
}

public sealed class MealRowViewModel(PlannedMeal meal, Action cook, Action remove)
{
    public string Text { get; } = $"{meal.Meal}: {meal.Title}";

    public bool IsCooked { get; } = meal.CookedAt is not null;

    public bool CanCook => !IsCooked;

    public IRelayCommand CookCommand { get; } = new RelayCommand(cook);

    public IRelayCommand RemoveCommand { get; } = new RelayCommand(remove);
}

public sealed class RecipeRowViewModel(Recipe recipe, Action edit, Action delete)
{
    public string Name { get; } = recipe.Name;

    public string Summary { get; } = string.Join(" · ", new[]
    {
        recipe.Servings is { } servings ? $"{servings} servings" : null,
        recipe.Ingredients.Count == 1 ? "1 ingredient" : $"{recipe.Ingredients.Count} ingredients",
    }.OfType<string>());

    public IRelayCommand EditCommand { get; } = new RelayCommand(edit);

    public IRelayCommand DeleteCommand { get; } = new RelayCommand(delete);
}
