package com.camorip.slider;

import java.util.HashMap;
import java.util.Map;

public final class TouchState {
    private final Map<Integer, Long> contacts = new HashMap<>();

    public void set(int pointerId, float x, float y, float width, float height, int columns, float sensitivity) {
        if ((columns != 16 && columns != 32) || !Float.isFinite(sensitivity) || sensitivity < 0 || sensitivity > 0.5f)
            throw new IllegalArgumentException("Invalid slider layout");
        long mask = 0;
        if (Float.isFinite(x) && Float.isFinite(y) && width > 0 && height > 0 && x >= 0 && x < width && y >= 0 && y < height) {
            int rows = 32 / columns;
            float cellWidth = width / columns, cellHeight = height / rows;
            float radius = cellWidth * sensitivity;
            for (int column = 0; column < columns; column++) {
                for (int row = 0; row < rows; row++) {
                    float nearX = Math.max(column * cellWidth, Math.min(x, (column + 1) * cellWidth));
                    float nearY = Math.max(row * cellHeight, Math.min(y, (row + 1) * cellHeight));
                    float dx = x - nearX, dy = y - nearY;
                    if (dx * dx + dy * dy <= radius * radius)
                        mask |= 1L << (31 - column * rows - row);
                }
            }
        }
        contacts.put(pointerId, mask);
    }

    public void remove(int pointerId) { contacts.remove(pointerId); }
    public void clear() { contacts.clear(); }
    public int count() { return contacts.size(); }
    public long mask() {
        long mask = 0;
        for (long contact : contacts.values()) mask |= contact;
        return mask;
    }
}
