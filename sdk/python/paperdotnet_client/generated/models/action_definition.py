from __future__ import annotations
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union

if TYPE_CHECKING:
    from .json_object import JsonObject

@dataclass
class ActionDefinition(AdditionalDataHolder, Parsable):
    """
    An action with its inputs (strings may contain tokens such as `{title}`).
    """
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The inputs property
    inputs: Optional[JsonObject] = None
    # The type property
    type: Optional[str] = None
    
    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> ActionDefinition:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: ActionDefinition
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return ActionDefinition()
    
    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        from .json_object import JsonObject

        from .json_object import JsonObject

        fields: dict[str, Callable[[Any], None]] = {
            "inputs": lambda n : setattr(self, 'inputs', n.get_object_value(JsonObject)),
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
        writer.write_object_value("inputs", self.inputs)
        writer.write_str_value("type", self.type)
        writer.write_additional_data_value(self.additional_data)
    

