from __future__ import annotations
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union

if TYPE_CHECKING:
    from .json_object import JsonObject

@dataclass
class AutomationStep(AdditionalDataHolder, Parsable):
    """
    A step of an automation (EVT-07, EVT-08). Assignees and recipients are user names, `group:Name`,`field:fieldName` (a person field of the item) or `creator`.
    """
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The action property
    action: Optional[str] = None
    # The assignees property
    assignees: Optional[list[str]] = None
    # The dueInHours property
    due_in_hours: Optional[float] = None
    # The else property
    else_: Optional[list[AutomationStep]] = None
    # The escalateTo property
    escalate_to: Optional[list[str]] = None
    # The filter property
    filter: Optional[str] = None
    # The hours property
    hours: Optional[float] = None
    # The inputs property
    inputs: Optional[JsonObject] = None
    # The is property
    is_: Optional[str] = None
    # The name property
    name: Optional[str] = None
    # The step property
    step: Optional[str] = None
    # The then property
    then: Optional[list[AutomationStep]] = None
    # The title property
    title: Optional[str] = None
    # The type property
    type: Optional[str] = None
    
    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> AutomationStep:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: AutomationStep
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return AutomationStep()
    
    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        from .json_object import JsonObject

        from .json_object import JsonObject

        fields: dict[str, Callable[[Any], None]] = {
            "action": lambda n : setattr(self, 'action', n.get_str_value()),
            "assignees": lambda n : setattr(self, 'assignees', n.get_collection_of_primitive_values(str)),
            "dueInHours": lambda n : setattr(self, 'due_in_hours', n.get_float_value()),
            "else": lambda n : setattr(self, 'else_', n.get_collection_of_object_values(AutomationStep)),
            "escalateTo": lambda n : setattr(self, 'escalate_to', n.get_collection_of_primitive_values(str)),
            "filter": lambda n : setattr(self, 'filter', n.get_str_value()),
            "hours": lambda n : setattr(self, 'hours', n.get_float_value()),
            "inputs": lambda n : setattr(self, 'inputs', n.get_object_value(JsonObject)),
            "is": lambda n : setattr(self, 'is_', n.get_str_value()),
            "name": lambda n : setattr(self, 'name', n.get_str_value()),
            "step": lambda n : setattr(self, 'step', n.get_str_value()),
            "then": lambda n : setattr(self, 'then', n.get_collection_of_object_values(AutomationStep)),
            "title": lambda n : setattr(self, 'title', n.get_str_value()),
            "type": lambda n : setattr(self, 'type', n.get_str_value()),
        }
        return fields
    
    def serialize(self,writer: SerializationWriter) -> None:
        """
        Serializes information the current object
        param writer: Serialization writer to use to serialize this model
        Returns: None
        """
        if writer is None:
            raise TypeError("writer cannot be null.")
        writer.write_str_value("action", self.action)
        writer.write_collection_of_primitive_values("assignees", self.assignees)
        writer.write_float_value("dueInHours", self.due_in_hours)
        writer.write_collection_of_object_values("else", self.else_)
        writer.write_collection_of_primitive_values("escalateTo", self.escalate_to)
        writer.write_str_value("filter", self.filter)
        writer.write_float_value("hours", self.hours)
        writer.write_object_value("inputs", self.inputs)
        writer.write_str_value("is", self.is_)
        writer.write_str_value("name", self.name)
        writer.write_str_value("step", self.step)
        writer.write_collection_of_object_values("then", self.then)
        writer.write_str_value("title", self.title)
        writer.write_str_value("type", self.type)
        writer.write_additional_data_value(self.additional_data)
    

